using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using UnnamedGame.Assets;
using UnnamedGame.Audio;
using UnnamedGame.Graphics;
using UnnamedGame.Platform;
using UnnamedGame.Sim;

namespace UnnamedGame;

public sealed class Game : IDisposable
{
    private const float FixedTimestep = 1f / 120f;
    private const float MouseSensitivity = 0.0022f;
    private const int MaxDynamics = 400;

    private const int VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_SHIFT = 0x10, VK_R = 0x52, VK_F = 0x46, VK_L = 0x4C, VK_E = 0x45;

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
    private readonly List<DrawCommand> _overlay = [];
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
            // Whatever time is left over in the accumulator is how far past the last
            // simulated state we are; render that fraction of the way to the current one.
            Render(Math.Clamp(_accumulator / FixedTimestep, 0f, 1f));
            ReportFps(frameTime);
        }
    }

    private void HandleInput(float dt)
    {
        if (_window.WasKeyPressed(VK_ESCAPE))
            _window.SetMouseCapture(!_window.MouseCaptured);

        if (!_window.MouseCaptured)
        {
            if (_window.WasMousePressed()) _window.SetMouseCapture(true);
            return;
        }

        _player.Look(-_window.MouseDeltaX * MouseSensitivity, -_window.MouseDeltaY * MouseSensitivity);

        if (_window.WasKeyPressed(VK_E))
            ToggleVehicle();

        if (!_driving && _window.WasKeyPressed(VK_R))
            _player.Teleport(_level.SpawnPoint);

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
            _driving = false;
            return;
        }

        if (Vector3.Distance(_player.Position, _car.Position) > EnterDistance) return;
        _player.Disable();
        _player.Yaw = _car.Yaw;   // start out looking where the car is pointing
        _driving = true;
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
        if (!_window.MouseCaptured) { wish = Vector2.Zero; jump = false; }
        _driveThrottle = wish.Y;

        // Fixed timestep keeps the movement curves and the solver deterministic.
        _accumulator += frameTime;
        int steps = 0;
        while (_accumulator >= FixedTimestep && steps++ < 8)
        {
            _accumulator -= FixedTimestep;
            SnapshotPoses();
            _car.SnapshotPose();

            if (_driving)
                _car.Update(wish.Y, wish.X, jump, FixedTimestep);
            else
                _player.Move(wish, jump, FixedTimestep);

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

    /// <summary>Places the listener, then lets the sound logic follow the simulation state.</summary>
    private void UpdateAudio(float frameTime)
    {
        var forward = _player.Forward;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var listener = _driving ? Vector3.Transform(SeatOffset, _car.ChassisMatrix()) : _player.EyePosition;
        _audio.SetListener(listener, right);

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
        var eye = _driving
            ? Vector3.Transform(SeatOffset, chassis)
            : _player.InterpolatedEyePosition(alpha);
        var forward = _player.Forward;
        var view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            75f * MathF.PI / 180f, _renderer.AspectRatio, 0.05f, 300f);

        _scene.Clear();
        _glass.Clear();
        _overlay.Clear();

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
        AddCrosshair(eye, forward);

        // On foot it is a torch held below and right of the eye; behind the wheel the same
        // key drives the car's headlights, mounted at the bumper and aimed where the car goes.
        Spotlight? spotlight = null;
        if (_flashlightOn)
        {
            spotlight = _driving
                ? Spotlight.Headlights(Vector3.Transform(new Vector3(0f, -0.12f, 2.2f), chassis), _car.Forward)
                : Spotlight.Flashlight(
                    eye + Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY)) * 0.18f - new Vector3(0, 0.12f, 0),
                    forward);
        }

        _renderer.RenderFrame(_scene, _glass, _overlay, _level.UpdateLights(_time), spotlight, view, projection, eye);
    }

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

    /// <summary>Two small quads pinned in front of the camera — cheapest possible HUD.</summary>
    private void AddCrosshair(Vector3 eye, Vector3 forward)
    {
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);

        var basis = new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            forward.X, forward.Y, forward.Z, 0,
            0, 0, 0, 1);
        var center = eye + forward * 0.12f;
        var color = new Vector4(0.95f, 0.95f, 0.95f, 1f);

        _overlay.Add(new DrawCommand(_renderer.BoxMesh,
            Matrix4x4.CreateScale(0.0035f, 0.0004f, 0.0004f) * basis * Matrix4x4.CreateTranslation(center),
            color, Vector3.One, Checker: false));
        _overlay.Add(new DrawCommand(_renderer.BoxMesh,
            Matrix4x4.CreateScale(0.0004f, 0.0035f, 0.0004f) * basis * Matrix4x4.CreateTranslation(center),
            color, Vector3.One, Checker: false));
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
        _fpsTimer = 0;
        _frameCount = 0;
    }

    public void Dispose()
    {
        _sounds.Dispose();
        _audio.Dispose();
        _carModel.Dispose();
        _physics.Dispose();
        _renderer.Dispose();
    }
}
