using System;
using System.Text;

namespace DLS.Game.Updates
{
	// The script that replaces a desktop installation with a downloaded release. Rewired starts
	// it and quits; the script waits for Rewired to exit, copies the new files over the old ones,
	// deletes the download and starts the new version. If copying fails it leaves FailedMarker in
	// the download folder and starts the installed version again, which then reports the failure.
	public static class UpdateInstallScript
	{
		public const string FailedMarker = "install-failed.txt";

		public static string FileName(UpdatePlatform platform) => platform == UpdatePlatform.Windows ? "install-update.cmd" : "install-update.sh";

		public static string Create(UpdatePlatform platform, int processId, string stagingDir, string downloadDir, string installDir, string executableName)
		{
			return platform switch
			{
				UpdatePlatform.Windows => Windows(processId, stagingDir, downloadDir, installDir, executableName),
				UpdatePlatform.Linux => Linux(processId, stagingDir, downloadDir, installDir, executableName),
				_ => throw new ArgumentException($"No install script for {platform}")
			};
		}

		static string Windows(int processId, string stagingDir, string downloadDir, string installDir, string executableName)
		{
			foreach (string path in new[] { stagingDir, downloadDir, installDir, executableName })
			{
				if (path.IndexOfAny(new[] { '"', '%', '\r', '\n' }) >= 0) throw new ArgumentException($"Unsupported character in path: {path}");
			}

			StringBuilder s = new();
			s.AppendLine("@echo off");
			s.AppendLine("setlocal");
			s.AppendLine(":wait");
			s.AppendLine($"tasklist /FI \"PID eq {processId}\" 2>NUL | find \"{processId}\" >NUL");
			s.AppendLine("if not errorlevel 1 (");
			s.AppendLine("  ping -n 2 127.0.0.1 >NUL");
			s.AppendLine("  goto wait");
			s.AppendLine(")");
			// robocopy exit codes below 8 mean success.
			s.AppendLine($"robocopy \"{stagingDir}\" \"{installDir}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >NUL");
			s.AppendLine("if errorlevel 8 goto failed");
			s.AppendLine($"start \"\" \"{installDir}\\{executableName}\"");
			s.AppendLine($"rmdir /S /Q \"{downloadDir}\"");
			s.AppendLine("exit /b 0");
			// The window is hidden, so nobody would see a message here: leave a marker and start
			// the installed version, which shows the failure.
			s.AppendLine(":failed");
			s.AppendLine($"type nul > \"{downloadDir}\\{FailedMarker}\"");
			s.AppendLine($"start \"\" \"{installDir}\\{executableName}\"");
			s.AppendLine("exit /b 1");
			return s.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
		}

		static string Linux(int processId, string stagingDir, string downloadDir, string installDir, string executableName)
		{
			string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";
			string executable = Quote(installDir.TrimEnd('/') + "/" + executableName);

			StringBuilder s = new();
			s.Append("#!/bin/sh\n");
			s.Append($"while kill -0 {processId} 2>/dev/null; do sleep 0.5; done\n");
			s.Append($"if ! cp -R {Quote(stagingDir.TrimEnd('/') + "/.")} {Quote(installDir)}; then\n");
			s.Append($"  : > {Quote(downloadDir.TrimEnd('/') + "/" + FailedMarker)}\n");
			s.Append($"  {executable} >/dev/null 2>&1 &\n");
			s.Append("  exit 1\n");
			s.Append("fi\n");
			// Zip files do not keep the executable bit.
			s.Append($"chmod +x {executable}\n");
			s.Append($"rm -rf {Quote(downloadDir)}\n");
			s.Append($"{executable} >/dev/null 2>&1 &\n");
			return s.ToString();
		}
	}
}
