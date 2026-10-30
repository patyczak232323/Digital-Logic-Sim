using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace DLS.Game.Updates
{
	// Looks for a newer Rewired release on GitHub and installs it.
	// - Windows and Linux builds download the release zip, unpack it next to the download and
	//   start a small script that copies it over this installation once Rewired has quit, then
	//   starts the new version.
	// - Android opens the release's APK in the browser; Android's installer then asks to update.
	// - The editor, WebGL and other platforms open the release page instead.
	public static class AppUpdater
	{
		public enum State
		{
			Idle,
			Checking,
			UpToDate,
			Available,
			Downloading,
			Unpacking,
			Restarting,
			Failed
		}

		public static State Current { get; private set; } = State.Idle;
		public static string LatestVersion => latest?.Version;
		public static float DownloadProgress => download?.downloadProgress ?? (Current == State.Unpacking ? 1 : 0);
		public static string Error { get; private set; } = string.Empty;

		// Where checking makes sense at all: WebGL is updated by whoever hosts it.
		public static bool Supported => Application.platform != RuntimePlatform.WebGLPlayer;

		static ReleaseInfo latest;
		static UnityWebRequest check;
		static UnityWebRequest download;
		static Task unpack;
		static string downloadDir;
		static string stagingDir;

		public static UpdatePlatform Platform => Application.isEditor ? UpdatePlatform.Other : Application.platform switch
		{
			RuntimePlatform.WindowsPlayer => UpdatePlatform.Windows,
			RuntimePlatform.LinuxPlayer => UpdatePlatform.Linux,
			RuntimePlatform.Android => UpdatePlatform.Android,
			_ => UpdatePlatform.Other
		};

		public static string StatusText => Current switch
		{
			State.Checking => "Checking for updates...",
			State.UpToDate => $"Rewired v{Main.RewiredVersion} is the latest version",
			State.Available => $"Rewired v{LatestVersion} is available",
			State.Downloading => $"Downloading v{LatestVersion}: {Mathf.RoundToInt(DownloadProgress * 100)}%",
			State.Unpacking => $"Unpacking v{LatestVersion}...",
			State.Restarting => "Restarting with the new version...",
			State.Failed => "Update failed: " + Error,
			_ => string.Empty
		};

		public static string ActionLabel => Current switch
		{
			State.Available => Platform is UpdatePlatform.Windows or UpdatePlatform.Linux ? $"UPDATE TO v{LatestVersion}" : $"DOWNLOAD v{LatestVersion}",
			State.Idle or State.UpToDate or State.Failed => "CHECK FOR UPDATES",
			_ => null
		};

		public static bool Busy => Current is State.Checking or State.Downloading or State.Unpacking or State.Restarting;

		// The button next to the status: checks, or installs the update that was found.
		public static void RunAction()
		{
			if (Current == State.Available) Install();
			else if (!Busy) Check();
		}

		public static void Check()
		{
			if (!Supported || Busy) return;

			Error = string.Empty;
			check = UnityWebRequest.Get(ReleaseInfo.LatestReleaseApiUrl);
			check.SetRequestHeader("Accept", "application/vnd.github+json");
			check.SetRequestHeader("User-Agent", "Rewired-Updater");
			check.timeout = 20;
			check.SendWebRequest();
			Current = State.Checking;
		}

		public static void Install()
		{
			if (latest == null || Current != State.Available) return;

			UpdatePlatform platform = Platform;
			bool hasAsset = latest.TryGetAsset(platform, out string assetUrl, out _);

			if (platform == UpdatePlatform.Android && hasAsset)
			{
				// The browser downloads the APK; opening it asks Android to update Rewired.
				Application.OpenURL(assetUrl);
				return;
			}

			if (platform is UpdatePlatform.Windows or UpdatePlatform.Linux && hasAsset && InstallFolderIsWritable())
			{
				StartDownload(assetUrl);
				return;
			}

			Application.OpenURL(latest.PageUrl);
		}

		// Called every frame from Main.Update.
		public static void Update()
		{
			try
			{
				if (check != null && check.isDone) FinishCheck();
				if (download != null && download.isDone) FinishDownload();
				if (unpack != null && unpack.IsCompleted) FinishUnpack();
			}
			catch (Exception e)
			{
				Fail(e.Message);
			}
		}

		static void FinishCheck()
		{
			UnityWebRequest request = check;
			check = null;
			using (request)
			{
				if (request.result != UnityWebRequest.Result.Success)
				{
					Fail(request.responseCode == 404 ? "no release has been published yet" : request.error);
					return;
				}

				latest = ReleaseInfo.Parse(request.downloadHandler.text);
				Current = latest.IsNewerThan(Main.RewiredVersion) ? State.Available : State.UpToDate;
			}
		}

		static void StartDownload(string url)
		{
			downloadDir = Path.Combine(Application.temporaryCachePath, "RewiredUpdate");
			if (Directory.Exists(downloadDir)) Directory.Delete(downloadDir, true);
			Directory.CreateDirectory(downloadDir);

			string zipPath = Path.Combine(downloadDir, ReleaseInfo.AssetName(Platform));
			download = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET, new DownloadHandlerFile(zipPath) { removeFileOnAbort = true }, null);
			download.SetRequestHeader("User-Agent", "Rewired-Updater");
			download.SendWebRequest();
			Current = State.Downloading;
		}

		static void FinishDownload()
		{
			UnityWebRequest request = download;
			download = null;
			using (request)
			{
				if (request.result != UnityWebRequest.Result.Success)
				{
					Fail(request.error);
					return;
				}
			}

			string zipPath = Path.Combine(downloadDir, ReleaseInfo.AssetName(Platform));
			stagingDir = Path.Combine(downloadDir, "files");
			string expectedExecutable = ExecutableName;
			string staging = stagingDir;
			unpack = Task.Run(() =>
			{
				Unzip(zipPath, staging);
				if (!File.Exists(Path.Combine(staging, expectedExecutable))) throw new InvalidDataException($"the download does not contain {expectedExecutable}");
			});
			Current = State.Unpacking;
		}

		static void FinishUnpack()
		{
			Task task = unpack;
			unpack = null;
			if (task.IsFaulted)
			{
				Fail(task.Exception?.GetBaseException().Message ?? "could not unpack the download");
				return;
			}

			UpdatePlatform platform = Platform;
			string script = UpdateInstallScript.Create(platform, Process.GetCurrentProcess().Id, stagingDir, downloadDir, InstallFolder, ExecutableName);
			string scriptPath = Path.Combine(Application.temporaryCachePath, UpdateInstallScript.FileName(platform));
			File.WriteAllText(scriptPath, script);

			ProcessStartInfo start = platform == UpdatePlatform.Windows
				? new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"") { CreateNoWindow = true }
				: new ProcessStartInfo("/bin/sh", $"\"{scriptPath}\"");
			start.UseShellExecute = false;
			Process.Start(start);

			Current = State.Restarting;
			Application.Quit();
		}

		// Extracts with ZipArchive (always present, unlike ZipFile's helpers) and refuses entries
		// that would land outside the target folder.
		static void Unzip(string zipPath, string targetDir)
		{
			string root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
			using ZipArchive archive = new(File.OpenRead(zipPath), ZipArchiveMode.Read);
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				string path = Path.GetFullPath(Path.Combine(root, entry.FullName));
				if (!path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"unexpected path in the download: {entry.FullName}");

				if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
				{
					Directory.CreateDirectory(path);
					continue;
				}

				Directory.CreateDirectory(Path.GetDirectoryName(path));
				using Stream source = entry.Open();
				using FileStream destination = File.Create(path);
				source.CopyTo(destination);
			}
		}

		static void Fail(string message)
		{
			Error = string.IsNullOrEmpty(message) ? "unknown error" : message;
			Current = State.Failed;
			Debug.LogWarning("Rewired update: " + Error);
		}

		// The player's folder: <install>/<name>_Data is Application.dataPath.
		static string InstallFolder => Directory.GetParent(Application.dataPath).FullName;

		static string ExecutableName
		{
			get
			{
				string name = Path.GetFileName(Application.dataPath);
				if (name.EndsWith("_Data", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "_Data".Length);
				return Platform == UpdatePlatform.Windows ? name + ".exe" : name + ".x86_64";
			}
		}

		static bool InstallFolderIsWritable()
		{
			try
			{
				string probe = Path.Combine(InstallFolder, ".rewired-update-check");
				File.WriteAllText(probe, string.Empty);
				File.Delete(probe);
				return File.Exists(Path.Combine(InstallFolder, ExecutableName));
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
