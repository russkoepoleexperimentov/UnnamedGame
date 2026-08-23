using UnnamedGame;

Console.WriteLine("""
    UnnamedGame — MVP
      WASD      move (throttle, brake and steering when driving)
      Space     jump (hold to bunny-hop) / handbrake in the car
      Mouse     look
      LMB / F   fire a physics ball
      E         enter / leave the car
      L         flashlight on foot, headlights in the car
      R         respawn
      Esc       release the mouse (click the window to capture it)
    """);

using var game = new Game();
game.Run();
