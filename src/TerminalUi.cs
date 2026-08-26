using System;
using System.IO;

namespace SaiCont
{
    internal static class TerminalUi
    {
        private static readonly string[][] WordmarkLetters =
        {
            new[] { "#####", "  #  ", "  #  ", "  #  ", "  #  " },
            new[] { "#####", "#    ", "#### ", "#    ", "#####" },
            new[] { "#### ", "#   #", "#### ", "#  # ", "#   #" },
            new[] { "#   #", "## ##", "# # #", "#   #", "#   #" },
            new[] { "#####", "  #  ", "  #  ", "  #  ", "#####" },
            new[] { " ####", "#    ", " ### ", "    #", "#### " },
            new[] { " ### ", "#   #", "#####", "#   #", "#   #" },
            new[] { "#####", "  #  ", "  #  ", "  #  ", "#####" }
        };

        public static void PrintLandingPage()
        {
            bool interactive = !Console.IsOutputRedirected;
            ConsoleColor originalForeground = Console.ForegroundColor;
            ConsoleColor originalBackground = Console.BackgroundColor;

            try
            {
                if (interactive)
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.Clear();
                }

                SetColor(interactive, ConsoleColor.DarkYellow);
                WriteCentered("+---------+");
                WriteCentered("/#########/|");
                WriteCentered("+---------+ |");
                WriteCentered("|   ###   | |");
                WriteCentered("|   ###   | +");
                WriteCentered("|   ###   |/");
                WriteCentered("+---------+");
                Console.WriteLine();
                foreach (string line in BuildWordmark())
                {
                    WriteCentered(line);
                }

                SetColor(interactive, ConsoleColor.Gray);
                WriteCentered("SAICONT / TERMINAL CONTINUITY");
                Console.WriteLine();
                WriteRule();

                SetColor(interactive, ConsoleColor.DarkCyan);
                Console.WriteLine("  START");
                SetColor(interactive, ConsoleColor.Gray);
                Console.WriteLine("  --probe      inspect Cline/Codex; never send input");
                Console.WriteLine("  --dry-run    watch continuously; never send input");
                Console.WriteLine("  --watch      run guarded continuation");
                Console.WriteLine("  --self-test  run deterministic checks");
                Console.WriteLine();

                SetColor(interactive, ConsoleColor.DarkCyan);
                Console.WriteLine("  SAIPEN BASICS");
                SetColor(interactive, ConsoleColor.Gray);
                Console.WriteLine("  cc  continue    gg <goal>  new goal");
                Console.WriteLine("  ss  stop        sss        status");
                Console.WriteLine();

                SetColor(interactive, ConsoleColor.DarkCyan);
                Console.WriteLine("  CLINE BASICS");
                SetColor(interactive, ConsoleColor.Gray);
                Console.WriteLine("  Enter  submit       Esc     abort/close menu");
                Console.WriteLine("  Ctrl+C clear/exit   Ctrl+L  clear conversation");
                WriteRule();
                SetColor(interactive, ConsoleColor.DarkGreen);
                Console.WriteLine("  Safe first run: SAICONT.exe --probe");
            }
            finally
            {
                if (interactive)
                {
                    Console.ForegroundColor = originalForeground;
                    Console.BackgroundColor = originalBackground;
                }
            }
        }

        private static void WriteCentered(string value)
        {
            int width = 64;
            try
            {
                width = Math.Max(1, Console.WindowWidth);
            }
            catch (IOException)
            {
            }

            int padding = Math.Max(0, (width - value.Length) / 2);
            Console.WriteLine(new string(' ', padding) + value);
        }

        private static string[] BuildWordmark()
        {
            var lines = new string[5];
            for (int row = 0; row < lines.Length; row++)
            {
                var parts = new string[WordmarkLetters.Length];
                for (int letter = 0; letter < WordmarkLetters.Length; letter++)
                {
                    parts[letter] = WordmarkLetters[letter][row];
                }
                lines[row] = String.Join(" ", parts);
            }
            return lines;
        }

        private static void WriteRule()
        {
            Console.WriteLine("  +--------------------------------------------------------+");
        }

        private static void SetColor(bool interactive, ConsoleColor color)
        {
            if (interactive)
            {
                Console.ForegroundColor = color;
            }
        }
    }
}
