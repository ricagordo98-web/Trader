using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class IniciarTrader
{
    [STAThread]
    private static void Main()
    {
        string folder = AppDomain.CurrentDomain.BaseDirectory;
        string bat = Path.Combine(folder, "Iniciar-Trader.bat");

        if (!File.Exists(bat))
        {
            MessageBox.Show("Não encontrei o Iniciar-Trader.bat em " + folder, "Trader", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Process.Start(new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\"\"")
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
}
