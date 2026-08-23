using UnnamedGame;

Console.WriteLine("""
    UnnamedGame — MVP
      WASD      move
      Space     jump (hold to bunny-hop)
      Mouse     look
      LMB / F   fire a physics ball
      R         respawn
      Esc       release the mouse (click the window to capture it)
    """);

using var game = new Game();
game.Run();
