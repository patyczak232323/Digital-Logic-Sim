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
	// - Android downloads the release's APK and installs it with PackageInstaller
	//   (Assets/Plugins/Android/RewiredApkInstaller.java). Android 12+ lets Rewired update itself
	//   without a dialog once installs from Rewired are allowed; the update then closes the app.
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
			Installing,
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
		static long expectedDownloadSize;
		static string stagedExecutable;
		static AndroidJavaClass androidInstaller;
		static string androidStatus = string.Empty;

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
			State.Installing => androidStatus == "confirm" ? $"Confirm the update to v{LatestVersion} in Android's dialog" : $"Installing v{LatestVersion}...",
			State.Restarting => "Restarting with the new version...",
			State.Failed => "Update failed: " + Error,
			_ => string.Empty
		};

		public static string ActionLabel => Current switch
		{
			State.Available => Platform is UpdatePlatform.Windows or UpdatePlatform.Linux or UpdatePlatform.Android ? $"UPDATE TO v{LatestVersion}" : $"DOWNLOAD v{LatestVersion}",
			State.Idle or State.UpToDate or State.Failed => "CHECK FOR UPDATES",
			_ => null
		};

		public static bool Busy => Current is State.Checking or State.Downloading or State.Unpacking or State.Installing or State.Restarting;

		// The button next to the status: checks, or installs the update that was found.
		public static void RunAction()
		{
			if (Current == State.Available) Install();
			else if (!Busy) Check();
		}

		public static void Check()
		{
			if (!Supported || Busy) return;
			if (ReportFailedInstall()) return;

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
			bool hasAsset = latest.TryGetAsset(platform, out string assetUrl, out long assetSize);

			if (platform == UpdatePlatform.Android && hasAsset)
			{
				expectedDownloadSize = assetSize;
				StartDownload(assetUrl);
				return;
			}

			if (platform is UpdatePlatform.Windows or UpdatePlatform.Linux && hasAsset && InstallFolderIsWritable())
			{
				expectedDownloadSize = assetSize;
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
				if (Current == State.Installing) PollAndroidInstall();
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
			long downloadedSize = new FileInfo(zipPath).Length;
			if (expectedDownloadSize > 0 && downloadedSize != expectedDownloadSize)
			{
				Fail($"the download is incomplete ({downloadedSize} of {expectedDownloadSize} bytes)");
				return;
			}

			if (Platform == UpdatePlatform.Android)
			{
				StartAndroidInstall(zipPath);
				return;
			}

			stagingDir = Path.Combine(downloadDir, "files");
			string[] candidates = ExecutableCandidates;
			string staging = stagingDir;
			stagedExecutable = null;
			unpack = Task.Run(() =>
			{
				Unzip(zipPath, staging);
				stagedExecutable = Array.Find(candidates, name => File.Exists(Path.Combine(staging, name)));
				if (stagedExecutable == null) throw new InvalidDataException($"the download does not contain {candidates[0]}");
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
			string script = UpdateInstallScript.Create(platform, Process.GetCurrentProcess().Id, stagingDir, downloadDir, InstallFolder, stagedExecutable);
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

		// Hands the APK to Android's PackageInstaller, which replaces the running app.
		static void StartAndroidInstall(string apkPath)
		{
			using AndroidJavaClass unityPlayer = new("com.unity3d.player.UnityPlayer");
			using AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
			androidInstaller ??= new AndroidJavaClass("com.rewired.updater.RewiredApkInstaller");
			androidStatus = string.Empty;
			androidInstaller.CallStatic("install", activity, apkPath);
			Current = State.Installing;
		}

		static void PollAndroidInstall()
		{
			androidStatus = androidInstaller?.CallStatic<string>("getStatus") ?? string.Empty;
			if (androidStatus.StartsWith("failed:", StringComparison.Ordinal)) Fail(androidStatus.Substring("failed:".Length));
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

		// The install script leaves a marker in the download folder when it could not copy the
		// update and started this version again instead.
		static bool ReportFailedInstall()
		{
			string folder = Path.Combine(Application.temporaryCachePath, "RewiredUpdate");
			string marker = Path.Combine(folder, UpdateInstallScript.FailedMarker);
			if (!File.Exists(marker)) return false;

			try
			{
				Directory.Delete(folder, true);
			}
			catch (Exception)
			{
				// Left for the next download, which clears the folder first.
			}

			Fail($"the new version could not be copied into {InstallFolder}. Close anything using that folder and try again");
			return true;
		}

		// The player's folder: <install>/<name>_Data is Application.dataPath.
		static string InstallFolder => Directory.GetParent(Application.dataPath).FullName;

		// Possible names of the player executable: Unity names the Linux player after the build
		// without an extension (DLSRewired), or with .x86_64 when built from the editor.
		static string[] ExecutableCandidates
		{
			get
			{
				string name = Path.GetFileName(Application.dataPath);
				if (name.EndsWith("_Data", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "_Data".Length);
				return Platform == UpdatePlatform.Windows ? new[] { name + ".exe" } : new[] { name, name + ".x86_64" };
			}
		}

		static bool InstallFolderIsWritable()
		{
			try
			{
				string probe = Path.Combine(InstallFolder, ".rewired-update-check");
				File.WriteAllText(probe, string.Empty);
				File.Delete(probe);
				return Array.Exists(ExecutableCandidates, name => File.Exists(Path.Combine(InstallFolder, name)));
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
