using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using UnnamedGame.Assets;
using UnnamedGame.Audio;
using UnnamedGame.Graphics;
using UnnamedGame.Platform;
using UnnamedGame.Sim;
using UnnamedGame.UI;

namespace UnnamedGame;

public sealed class Game : IDisposable
{
    private const float FixedTimestep = 1f / 120f;
    private const float MouseSensitivity = 0.0022f;
    private const int MaxDynamics = 400;

    private const int VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_SHIFT = 0x10, VK_R = 0x52, VK_F = 0x46, VK_L = 0x4C, VK_E = 0x45;
    private const int VK_TILDE = 0xC0;   // the ` / ~ key, and ё on a Russian layout
    private const int VK_CONTROL = 0x11;
    private static readonly int[] ConsoleKeys = [0x21, 0x22, 0x26, 0x28];   // PgUp, PgDn, Up, Down

    /// <summary>Driver's eye, relative to the chassis centre: left-hand drive, just behind the wheel.</summary>
    private static readonly Vector3 SeatOffset = new(0.35f, 0.58f, 0.30f);
    private const float EnterDistance = 3.4f;
    private const int VK_W = 0x57, VK_A = 0x41, VK_S = 0x53, VK_D = 0x44;

    private readonly GameWindow _window;
    private readonly Renderer _renderer;
    private readonly PhysicsWorld _physics;
    private readonly Level _level;
    private readonly Character _player;
    private readonly AudioEngine _audio;
    private readonly GameAudio _sounds;
    private readonly RenderModel _carModel;
    private readonly Vehicle _car;
    private readonly List<RenderModel.Part> _carBodyParts = [];
    private readonly List<RenderModel.Part>[] _carWheelParts = [[], [], [], []];
    private readonly List<RenderModel.Part> _carGlassParts = [];
    private bool _driving;

    private readonly List<DrawCommand> _scene = [];
    private readonly List<DrawCommand> _glass = [];
    private readonly DevConsole _console = new();
    private readonly FontAtlas _font;
    private readonly UiBatch _ui;
    private readonly Cvar _playerDebug;
    private readonly ViewEffects _view = new();
    private Cvar _bobScale, _carSway;

    // While driving these are the aim relative to the car, so the view turns with it.
    private float _carLookYaw, _carLookPitch;
    private Vector3 _viewForward = -Vector3.UnitZ;
    private Vector3 _viewRight = Vector3.UnitX;
    private float _measuredFps;
    private bool _flashlightOn = true;
    private float _time;

    private float _driveThrottle;
    private float _accumulator;
    private float _fireCooldown;
    private float _fpsTimer;
    private int _frameCount;

    public Game()
    {
        _window = new GameWindow("UnnamedGame — MVP", 1280, 720);
        _renderer = new Renderer(_window);
        _physics = new PhysicsWorld(new Vector3(0, -18f, 0));
        _level = new Level(_physics);
        _player = new Character(_physics, _level.SpawnPoint);

        _font = _renderer.CreateFont();
        _ui = new UiBatch(_font);
        _playerDebug = _console.RegisterCvar("player_debug", 0f, "0/1 overlay: position, speed, surface, state");
        _bobScale = _console.RegisterCvar("cl_bob", 1f, "head bob while walking, 0 disables");
        _carSway = _console.RegisterCvar("cl_carsway", 1f, "camera inertia in the car, 0 disables");
        RegisterConsoleCommands();

        _audio = new AudioEngine();
        _sounds = new GameAudio(_audio);

        var loadClock = Stopwatch.StartNew();
        var model = ModelLoader.Load(
            AssetPaths.Get("models", "vehicles", "lada vaz 2110.fbx"),
            AssetPaths.Get("textures", "vehicles"));
        _carModel = _renderer.CreateModel(model);
        _car = BuildVehicle();
        Console.WriteLine($"loaded car: {model.Parts.Count} parts ({_carGlassParts.Count} glass), " +
                          $"{model.Materials.Count} materials in {loadClock.ElapsedMilliseconds} ms");
        Console.WriteLine($"audio: {(_audio.IsAvailable ? "XAudio2" : "disabled")}, " +
                          $"{_sounds.LoadedFootstepClips} footstep clips");

        LoadConfig();
        _console.Print("UnnamedGame console. Type help for commands, ~ to close.");
        _console.Print($"car loaded: {model.Parts.Count} parts, audio: " +
                       $"{(_audio.IsAvailable ? "XAudio2" : "disabled")}");
    }

    /// <summary>
    /// Splits the loaded model into a rigid body group and the four wheels, and takes the
    /// suspension anchors straight from the wheel nodes so the physics matches the model.
    /// </summary>
    private Vehicle BuildVehicle()
    {
        var hubs = new Vector3[4];
        var found = new bool[4];

        foreach (var part in _carModel.Parts)
        {
            int wheel = WheelIndex(part.NodeName);
            if (part.IsGlass && wheel < 0)
            {
                _carGlassParts.Add(part);   // windows and light covers ride the body
                continue;
            }
            if (wheel < 0)
            {
                _carBodyParts.Add(part);
                continue;
            }

            _carWheelParts[wheel].Add(part);   // a wheel is several parts: tyre, rim, brake disc
            hubs[wheel] = part.Transform.Translation;
            found[wheel] = true;
        }

        if (Array.IndexOf(found, false) >= 0)
            throw new InvalidDataException("The car model is missing one of its KAMA_E224 wheel nodes.");

        // Anchors sit at chassis-centre height; the hub hangs SuspensionRest below at rest.
        float centreHeight = hubs.Average(h => h.Y) + Vehicle.SuspensionRest;
        var anchors = new Vector3[4];
        for (int i = 0; i < 4; i++)
            anchors[i] = new Vector3(hubs[i].X, 0f, hubs[i].Z);

        return new Vehicle(_physics, new Vector3(6.5f, centreHeight + 0.05f, 11f), 0.35f, anchors, centreHeight);
    }

    private static int WheelIndex(string nodeName) => nodeName switch
    {
        "KAMA_E224_LF" => 0,
        "KAMA_E224_RF" => 1,
        "KAMA_E224_LB" => 2,
        "KAMA_E224_RB" => 3,
        _ => -1,
    };

    public void Run()
    {
        var clock = Stopwatch.StartNew();
        double previous = clock.Elapsed.TotalSeconds;

        while (!_window.IsClosed)
        {
            double now = clock.Elapsed.TotalSeconds;
            float frameTime = MathF.Min((float)(now - previous), 0.25f);
            previous = now;

            _window.PumpEvents();
            if (_window.IsClosed) break;
            if (_window.Resized) _renderer.Resize();

            _time += frameTime;
            HandleInput(frameTime);
            StepSimulation(frameTime);
            UpdateAudio(frameTime);
            UpdateViewEffects(frameTime);
            _console.Update(frameTime);
            // Whatever time is left over in the accumulator is how far past the last
            // simulated state we are; render that fraction of the way to the current one.
            Render(Math.Clamp(_accumulator / FixedTimestep, 0f, 1f));
            ReportFps(frameTime);
        }
    }


    /// <summary>Console commands that need to reach into the game.</summary>
    private void RegisterConsoleCommands()
    {
        _console.RegisterCvar("snd_volume", 0.8f, "master volume, 0..1");
        _console.RegisterCvar("timescale", 1f, "simulation speed multiplier");

        _console.RegisterCommand("quit", _ => _window.Close(), "exit the game");

        _console.RegisterCommand("teleport", args =>
        {
            if (args.Length < 4) { _console.PrintError("usage: teleport <x> <y> <z>"); return; }
            var target = new Vector3(ParseFloat(args[1]), ParseFloat(args[2]), ParseFloat(args[3]));
            if (_driving) _console.PrintError("cannot teleport while driving");
            else { _player.Teleport(target); _console.Print($"teleported to {Format(target)}"); }
        }, "move the player to a position");

        _console.RegisterCommand("respawn", _ =>
        {
            if (_driving) ToggleVehicle();
            _player.Teleport(_level.SpawnPoint);
            _console.Print("respawned");
        }, "return to the spawn point");

        _console.RegisterCommand("spawn", args =>
        {
            int count = args.Length > 1 ? (int)ParseFloat(args[1]) : 1;
            var direction = _player.Forward;
            var origin = _driving ? Vector3.Transform(SeatOffset, _car.ChassisMatrix()) : _player.EyePosition;
            for (int i = 0; i < Math.Clamp(count, 1, 64); i++)
                _level.SpawnBall(_physics, origin + direction * (0.8f + i * 0.35f), direction * 12f);
            TrimDynamics();
            _console.Print($"spawned {Math.Clamp(count, 1, 64)} balls");
        }, "fire n physics balls");

        _console.RegisterCommand("car", _ =>
        {
            var beside = _car.ToWorld(new Vector3(2.2f, 0.2f, 0f));
            if (_driving) { _console.PrintError("already driving"); return; }
            _player.Teleport(beside + Vector3.UnitY * (Character.CylinderHalfLength + Character.Radius));
            _console.Print($"moved to the car at {Format(_car.Position)}");
        }, "walk over to the car");

        _console.RegisterCommand("noclip", _ =>
        {
            if (_driving) { _console.PrintError("noclip: get out of the car first"); return; }
            _player.SetNoclip(!_player.Noclip);
            _console.Print(_player.Noclip
                ? "noclip ON - space/ctrl to rise and sink, shift to sprint"
                : "noclip OFF");
        }, "toggle free flight through geometry");

        _console.RegisterCommand("saveconfig", _ =>
        {
            _console.SaveConfig(AssetPaths.ConfigFile);
            _console.Print($"wrote {AssetPaths.ConfigFile}");
        }, "write variables and bindings to config.cfg");

        _console.RegisterCommand("pos", _ =>
        {
            var position = _driving ? _car.Position : _player.Position;
            _console.Print($"{(_driving ? "car" : "player")} {Format(position)}  yaw {_player.Yaw:F2}");
        }, "print the current position");
    }

    /// <summary>Runs config.cfg at startup, creating a starter one the first time.</summary>
    private void LoadConfig()
    {
        if (!File.Exists(AssetPaths.ConfigFile))
        {
            File.WriteAllLines(AssetPaths.ConfigFile,
            [
                "// UnnamedGame config - executed at startup, rewritten on exit.",
                "// Edit freely; comments outside this header are not preserved.",
                "",
                "player_debug 0",
                "snd_volume 0.8",
                "timescale 1",
                "",
                "bind f1 player_debug 1",
                "bind f2 player_debug 0",
                "bind n noclip",
                "bind b spawn 5",
                "bind r respawn",
            ]);
        }

        _console.ExecuteFile(AssetPaths.ConfigFile);
    }

    private static float ParseFloat(string text)
        => float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static string Format(Vector3 v) => $"{v.X:F2} {v.Y:F2} {v.Z:F2}";

    private void HandleInput(float dt)
    {
        // The console eats input while it is open, the way it always has.
        foreach (var key in ConsoleKeys)
            if (_window.WasKeyPressed(key)) _console.HandleKey(key);

        if (_window.WasKeyPressed(VK_TILDE)) _console.Toggle();

        foreach (var c in _window.TypedCharacters)
            _console.HandleChar(c);

        if (_console.IsOpen)
        {
            if (_window.WasKeyPressed(VK_ESCAPE)) _console.Close();
            _audio.SetMasterVolume(_console.Find("snd_volume").Value);
            return;
        }

        _audio.SetMasterVolume(_console.Find("snd_volume").Value);

        // Bindings fire on the frame the key goes down, console closed only.
        foreach (var (key, command) in _console.Bindings)
            if (_window.WasKeyPressed(key))
                _console.Execute(command);

        if (_window.WasKeyPressed(VK_ESCAPE))
            _window.SetMouseCapture(!_window.MouseCaptured);

        if (!_window.MouseCaptured)
        {
            if (_window.WasMousePressed()) _window.SetMouseCapture(true);
            return;
        }

        float lookYaw = -_window.MouseDeltaX * MouseSensitivity;
        float lookPitch = -_window.MouseDeltaY * MouseSensitivity;
        if (_driving)
        {
            // Aim is stored relative to the car, so steering carries the view round with it.
            _carLookYaw = MathF.IEEERemainder(_carLookYaw + lookYaw, MathF.Tau);
            _carLookPitch = Math.Clamp(_carLookPitch + lookPitch, -1.2f, 1.2f);
        }
        else
        {
            _player.Look(lookYaw, lookPitch);
        }

        if (_window.WasKeyPressed(VK_E))
            ToggleVehicle();

        if (_window.WasKeyPressed(VK_L))
            _flashlightOn = !_flashlightOn;

        _fireCooldown -= dt;
        bool wantsFire = !_driving && (_window.WasMousePressed() || _window.IsKeyDown(VK_F));
        if (wantsFire && _fireCooldown <= 0)
        {
            _fireCooldown = 0.12f;
            var direction = _player.Forward;
            _level.SpawnBall(_physics, _player.EyePosition + direction * 0.7f, direction * 26f + _player.Velocity * 0.4f);
            TrimDynamics();
        }
    }

    /// <summary>Gets in if the player is standing next to the car, gets out if already driving.</summary>
    private void ToggleVehicle()
    {
        if (_driving)
        {
            // Step out on the driver's side, or the far side if that one is blocked.
            var left = _car.ToWorld(new Vector3(1.75f, 0.1f, 0.2f));    // driver's side
            var right = _car.ToWorld(new Vector3(-1.75f, 0.1f, 0.2f));
            var exit = IsFree(left) ? left : IsFree(right) ? right : _car.Position + Vector3.UnitY * 2.2f;

            _player.Enable(exit + new Vector3(0, Character.CylinderHalfLength + Character.Radius, 0));

            // Carry the direction the driver was looking in out of the car with them.
            _player.Yaw = MathF.Atan2(-_viewForward.X, -_viewForward.Z);
            _player.Pitch = MathF.Asin(Math.Clamp(_viewForward.Y, -1f, 1f));
            _driving = false;
            _view.ResetVehicle();
            return;
        }

        if (Vector3.Distance(_player.Position, _car.Position) > EnterDistance) return;
        _player.SetNoclip(false);
        _player.Disable();
        _carLookYaw = 0f;         // start out looking straight down the bonnet
        _carLookPitch = 0f;
        _driving = true;
        _view.ResetVehicle();
    }

    /// <summary>Rough check that a capsule would fit where the player is about to be put.</summary>
    private bool IsFree(Vector3 position)
    {
        var centre = position + new Vector3(0, Character.CylinderHalfLength + Character.Radius, 0);
        return !_physics.RayCastAny(centre, Vector3.UnitY, Character.CylinderHalfLength)
            && !_physics.RayCastAny(centre, -Vector3.UnitY, Character.CylinderHalfLength + Character.Radius - 0.05f);
    }

    private void StepSimulation(float frameTime)
    {
        var wish = new Vector2(
            (_window.IsKeyDown(VK_D) ? 1 : 0) - (_window.IsKeyDown(VK_A) ? 1 : 0),
            (_window.IsKeyDown(VK_W) ? 1 : 0) - (_window.IsKeyDown(VK_S) ? 1 : 0));
        bool jump = _window.IsKeyDown(VK_SPACE);
        float vertical = (_window.IsKeyDown(VK_SPACE) ? 1 : 0) - (_window.IsKeyDown(VK_CONTROL) ? 1 : 0);
        bool sprint = _window.IsKeyDown(VK_SHIFT);
        bool crouch = _window.IsKeyDown(VK_CONTROL);
        if (!_window.MouseCaptured || _console.IsOpen) { wish = Vector2.Zero; jump = false; vertical = 0f; crouch = false; }
        _driveThrottle = wish.Y;

        // Fixed timestep keeps the movement curves and the solver deterministic.
        _accumulator += frameTime * Math.Clamp(_console.Find("timescale").Value, 0f, 8f);
        int steps = 0;
        while (_accumulator >= FixedTimestep && steps++ < 8)
        {
            _accumulator -= FixedTimestep;
            SnapshotPoses();
            _car.SnapshotPose();

            if (_driving)
                _car.Update(wish.Y, wish.X, jump, FixedTimestep);
            else
                _player.Move(wish, jump, FixedTimestep, vertical, sprint, crouch);

            _physics.Step(FixedTimestep);
        }
    }

    /// <summary>Records where every body was before the step, so rendering can interpolate.</summary>
    private void SnapshotPoses()
    {
        var props = CollectionsMarshal.AsSpan(_level.Dynamics);
        for (int i = 0; i < props.Length; i++)
            props[i].PreviousPose = _physics.Simulation.Bodies[props[i].Handle].Pose;
    }

    private void UpdateViewEffects(float frameTime)
    {
        if (_driving)
            _view.UpdateVehicle(_car.Velocity, _car.Forward, _car.Right, _carSway.Value, frameTime);
        else
            _view.UpdateWalk(_player.Velocity, _player.OnGround && !_player.Noclip, _bobScale.Value, frameTime);
    }

    /// <summary>Places the listener, then lets the sound logic follow the simulation state.</summary>
    private void UpdateAudio(float frameTime)
    {
        var listener = _driving ? Vector3.Transform(SeatOffset, _car.ChassisMatrix()) : _player.EyePosition;
        _audio.SetListener(listener, _viewRight);

        if (_driving)
            _sounds.SuspendFootsteps();
        else
            _sounds.UpdateFootsteps(_player.Velocity, _player.OnGround,
                _level.SurfaceOf(_player.GroundCollidable), frameTime);

        _sounds.UpdateVehicle(_car, _driving, _driveThrottle, frameTime);
        _audio.Update(frameTime);
    }

    private void Render(float alpha)
    {
        var chassis = _car.InterpolatedChassisMatrix(alpha);
        Vector3 eye, forward, up;

        if (_driving)
        {
            // The camera lives in the car's frame: its yaw, pitch and roll all come along, and
            // the mouse only adds an offset on top. Looking ahead means ahead down the bonnet.
            var carForward = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, chassis));
            var carRight = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitX, chassis));
            var carUp = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, chassis));

            float yaw = _carLookYaw;
            float pitch = Math.Clamp(_carLookPitch + _view.CarPitch, -1.35f, 1.35f);
            forward = ViewEffects.CarLookDirection(carForward, carRight, carUp, yaw, pitch);

            up = carUp;
            eye = Vector3.Transform(SeatOffset + new Vector3(-_view.CarOffset.X, _view.CarOffset.Y, _view.CarOffset.Z), chassis);
            up = RollAround(up, forward, _view.CarRoll);
        }
        else
        {
            eye = _player.InterpolatedEyePosition(alpha);
            forward = _player.Forward;
            var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            var viewUp = Vector3.Cross(right, forward);

            // Bob rides in view space so it leans with wherever the player is looking.
            eye += right * _view.WalkOffset.X + viewUp * _view.WalkOffset.Y + forward * _view.WalkOffset.Z;
            up = RollAround(Vector3.UnitY, forward, _view.WalkRoll);
        }

        _viewForward = forward;
        _viewRight = Vector3.Normalize(Vector3.Cross(forward, up));

        var view = Matrix4x4.CreateLookAt(eye, eye + forward, up);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            75f * MathF.PI / 180f, _renderer.AspectRatio, 0.05f, 300f);

        _scene.Clear();
        _glass.Clear();

        foreach (var prop in _level.Statics)
        {
            var world = Matrix4x4.CreateScale(prop.Size)
                * Matrix4x4.CreateFromQuaternion(prop.Orientation)
                * Matrix4x4.CreateTranslation(prop.Center);
            _scene.Add(new DrawCommand(_renderer.BoxMesh, world, prop.Color, new Vector3(2f), prop.Checker));
        }

        foreach (var prop in _level.Dynamics)
        {
            var current = _physics.Simulation.Bodies[prop.Handle].Pose;
            var position = Vector3.Lerp(prop.PreviousPose.Position, current.Position, alpha);
            var orientation = Quaternion.Slerp(prop.PreviousPose.Orientation, current.Orientation, alpha);

            if (prop.IsSphere)
            {
                var world = Matrix4x4.CreateScale(prop.Radius)
                    * Matrix4x4.CreateFromQuaternion(orientation)
                    * Matrix4x4.CreateTranslation(position);
                _scene.Add(new DrawCommand(_renderer.SphereMesh, world, prop.Color, Vector3.One, Checker: false));
            }
            else
            {
                var world = Matrix4x4.CreateScale(prop.Size)
                    * Matrix4x4.CreateFromQuaternion(orientation)
                    * Matrix4x4.CreateTranslation(position);
                _scene.Add(new DrawCommand(_renderer.BoxMesh, world, prop.Color, prop.Size * 0.5f, Checker: true));
            }
        }

        AddCar(chassis);

        // On foot it is a torch held below and right of the eye; behind the wheel the same
        // key drives the car's headlights, mounted at the bumper and aimed where the car goes.
        Spotlight? spotlight = null;
        if (_flashlightOn)
        {
            spotlight = _driving
                ? Spotlight.Headlights(Vector3.Transform(new Vector3(0f, -0.12f, 2.2f), chassis), _car.Forward)
                : Spotlight.Flashlight(eye + _viewRight * 0.18f - new Vector3(0, 0.12f, 0), forward);
        }

        BuildUi();
        _renderer.RenderFrame(_scene, _glass, _ui, _level.UpdateLights(_time), spotlight, view, projection, eye);
    }

    /// <summary>Tilts an up vector around the view axis.</summary>
    private static Vector3 RollAround(Vector3 up, Vector3 forward, float angle)
        => angle == 0f ? up : Vector3.Normalize(Vector3.Transform(up, Matrix4x4.CreateFromAxisAngle(forward, angle)));

    /// <summary>Queues the car: the shell rides the chassis, each wheel gets its own transform.</summary>
    private void AddCar(in Matrix4x4 chassis)
    {
        // The model's origin is on the ground; the physics body's origin is its centre of mass.
        var shell = Matrix4x4.CreateTranslation(0, -_car.CentreHeight, 0) * chassis;

        foreach (var part in _carBodyParts)
            _scene.Add(new DrawCommand(part.Mesh, part.Transform * shell, part.Color, Vector3.One, false, part.Texture));

        foreach (var part in _carGlassParts)
            _glass.Add(new DrawCommand(part.Mesh, part.Transform * shell, part.Color, Vector3.One, false, part.Texture));

        for (int i = 0; i < _carWheelParts.Length; i++)
        {
            var hub = _car.WheelTransform(i, chassis);
            foreach (var part in _carWheelParts[i])
            {
                var meshOrientation = part.Transform;
                meshOrientation.Translation = Vector3.Zero;   // the hub position comes from the suspension
                _scene.Add(new DrawCommand(part.Mesh, meshOrientation * hub, part.Color, Vector3.One, false, part.Texture));
            }
        }
    }

    /// <summary>Builds this frame's 2D layer: crosshair, debug overlay, console.</summary>
    private void BuildUi()
    {
        _ui.Clear();

        int width = _window.Width;
        int height = _window.Height;

        if (!_console.IsOpen)
        {
            var white = new Vector4(0.95f, 0.95f, 0.95f, 0.85f);
            float cx = width * 0.5f, cy = height * 0.5f;
            _ui.FillRect(cx - 7f, cy - 1f, 14f, 2f, white);
            _ui.FillRect(cx - 1f, cy - 7f, 2f, 14f, white);
        }

        // Hidden while the console is up: the two would sit on top of each other.
        if (_playerDebug.Bool && !_console.IsVisible) DrawDebugOverlay();

        _console.Draw(_ui, width, height);
    }

    /// <summary>player_debug 1: where the player is, how fast, on what, and in what state.</summary>
    private void DrawDebugOverlay()
    {
        var label = new Vector4(0.62f, 0.70f, 0.80f, 1f);
        var value = new Vector4(0.95f, 0.95f, 0.88f, 1f);
        var panel = new Vector4(0.02f, 0.03f, 0.05f, 0.55f);

        bool onGround = _player.OnGround;
        var position = _driving ? _car.Position : _player.Position;
        var velocity = _driving ? _car.Velocity : _player.Velocity;
        float horizontal = new Vector2(velocity.X, velocity.Z).Length();

        string state = _driving
            ? (_car.Wheels.ToArray().Count(w => w.OnGround) is var wheels && wheels > 0 ? $"driving ({wheels}/4 wheels down)" : "driving (airborne)")
            : _player.Noclip ? "noclip (no collision)"
            : $"on foot ({(_player.IsCrouching ? "crouched, " : "")}{(onGround ? "ground" : "air")})";
        string surface = _driving || _player.Noclip
            ? "-"
            : _level.SurfaceOf(_player.GroundCollidable).ToString();

        (string Label, string Value)[] rows =
        [
            ("position", Format(position)),
            ("velocity", $"{Format(velocity)}"),
            ("speed", $"{horizontal,6:F2} m/s horizontal   {velocity.Length(),6:F2} m/s total   {horizontal * 3.6f,6:F1} km/h"),
            ("facing", $"yaw {MathF.Atan2(-_viewForward.X, -_viewForward.Z) * 180f / MathF.PI,7:F1} " +
                       $"pitch {MathF.Asin(Math.Clamp(_viewForward.Y, -1f, 1f)) * 180f / MathF.PI,6:F1} (deg)   dir {Format(_viewForward)}"),
            ("state", state),
            ("surface", surface),
            ("ground", _driving || _player.Noclip ? "-" : $"normal {Format(_player.GroundNormal)}"),
            ("frame", $"{_measuredFps,5:F1} fps   bodies {_level.Dynamics.Count}   lights {_level.LightCount}"),
        ];

        float line = _ui.LineHeight;
        float x = 12f, y = 12f;
        float labelWidth = _font.Advance * 10f;
        float widest = rows.Max(r => _font.MeasureWidth(r.Value)) + labelWidth;

        _ui.FillRect(x - 6f, y - 5f, widest + 16f, rows.Length * line + 10f, panel);
        foreach (var (name, text) in rows)
        {
            _ui.DrawText(name, x, y, label);
            _ui.DrawText(text, x + labelWidth, y, value);
            y += line;
        }
    }

    /// <summary>Keeps the body count bounded by retiring the oldest spawned balls.</summary>
    private void TrimDynamics()
    {
        while (_level.Dynamics.Count > MaxDynamics)
        {
            int index = _level.Dynamics.FindIndex(p => p.IsSphere);
            if (index < 0) return;
            _physics.Simulation.Bodies.Remove(_level.Dynamics[index].Handle);
            _level.Dynamics.RemoveAt(index);
        }
    }

    private void ReportFps(float frameTime)
    {
        _frameCount++;
        _fpsTimer += frameTime;
        if (_fpsTimer < 1f) return;
        _measuredFps = _frameCount / _fpsTimer;

        if (_driving)
        {
            Console.WriteLine($"{_frameCount / _fpsTimer,6:F1} fps | driving {_car.ForwardSpeed * 3.6f,6:F1} km/h | " +
                              $"bodies {_level.Dynamics.Count}");
        }
        else
        {
            var speed = new Vector3(_player.Velocity.X, 0, _player.Velocity.Z).Length();
            Console.WriteLine($"{_frameCount / _fpsTimer,6:F1} fps | speed {speed,5:F2} m/s | " +
                              $"{(_player.OnGround ? "ground" : "air   ")} | bodies {_level.Dynamics.Count}");
        }
        _measuredFps = _frameCount / _fpsTimer;
        _fpsTimer = 0;
        _frameCount = 0;
    }

    public void Dispose()
    {
        _console.SaveConfig(AssetPaths.ConfigFile);
        _font.Dispose();
        _sounds.Dispose();
        _audio.Dispose();
        _carModel.Dispose();
        _physics.Dispose();
        _renderer.Dispose();
    }
}
