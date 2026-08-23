using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using UnnamedGame.Graphics;
using UnnamedGame.Platform;
using UnnamedGame.Sim;

namespace UnnamedGame;

public sealed class Game : IDisposable
{
    private const float FixedTimestep = 1f / 120f;
    private const float MouseSensitivity = 0.0022f;
    private const int MaxDynamics = 400;

    private const int VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_SHIFT = 0x10, VK_R = 0x52, VK_F = 0x46, VK_L = 0x4C;
    private const int VK_W = 0x57, VK_A = 0x41, VK_S = 0x53, VK_D = 0x44;

    private readonly GameWindow _window;
    private readonly Renderer _renderer;
    private readonly PhysicsWorld _physics;
    private readonly Level _level;
    private readonly Character _player;

    private readonly List<DrawCommand> _scene = [];
    private readonly List<DrawCommand> _overlay = [];
    private bool _flashlightOn = true;
    private float _time;

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
    }

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

        if (_window.WasKeyPressed(VK_R))
            _player.Teleport(_level.SpawnPoint);

        if (_window.WasKeyPressed(VK_L))
            _flashlightOn = !_flashlightOn;

        _fireCooldown -= dt;
        bool wantsFire = _window.WasMousePressed() || _window.IsKeyDown(VK_F);
        if (wantsFire && _fireCooldown <= 0)
        {
            _fireCooldown = 0.12f;
            var direction = _player.Forward;
            _level.SpawnBall(_physics, _player.EyePosition + direction * 0.7f, direction * 26f + _player.Velocity * 0.4f);
            TrimDynamics();
        }
    }

    private void StepSimulation(float frameTime)
    {
        var wish = new Vector2(
            (_window.IsKeyDown(VK_D) ? 1 : 0) - (_window.IsKeyDown(VK_A) ? 1 : 0),
            (_window.IsKeyDown(VK_W) ? 1 : 0) - (_window.IsKeyDown(VK_S) ? 1 : 0));
        bool jump = _window.IsKeyDown(VK_SPACE);
        if (!_window.MouseCaptured) { wish = Vector2.Zero; jump = false; }

        // Fixed timestep keeps the movement curves and the solver deterministic.
        _accumulator += frameTime;
        int steps = 0;
        while (_accumulator >= FixedTimestep && steps++ < 8)
        {
            _accumulator -= FixedTimestep;
            SnapshotPoses();
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

    private void Render(float alpha)
    {
        var eye = _player.InterpolatedEyePosition(alpha);
        var forward = _player.Forward;
        var view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            75f * MathF.PI / 180f, _renderer.AspectRatio, 0.05f, 300f);

        _scene.Clear();
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

        AddCrosshair(eye, forward);

        // Held slightly below and right of the eye, the way a carried torch would be.
        Spotlight? flashlight = _flashlightOn
            ? Spotlight.Flashlight(eye + Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY)) * 0.18f - new Vector3(0, 0.12f, 0), forward)
            : null;

        _renderer.RenderFrame(_scene, _overlay, _level.UpdateLights(_time), flashlight, view, projection, eye);
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

        var speed = new Vector3(_player.Velocity.X, 0, _player.Velocity.Z).Length();
        Console.WriteLine($"{_frameCount / _fpsTimer,6:F1} fps | speed {speed,5:F2} m/s | " +
                          $"{(_player.OnGround ? "ground" : "air   ")} | bodies {_level.Dynamics.Count}");
        _fpsTimer = 0;
        _frameCount = 0;
    }

    public void Dispose()
    {
        _physics.Dispose();
        _renderer.Dispose();
    }
}
