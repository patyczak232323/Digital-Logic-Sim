using DLS.Game.Updates;
using NUnit.Framework;

namespace DLS.Tests
{
	public class UpdateTests
	{
		const string ReleaseJson = @"{
  ""tag_name"": ""v0.5.0"",
  ""html_url"": ""https://github.com/patyczak232323/Rewired/releases/tag/v0.5.0"",
  ""assets"": [
    { ""name"": ""DLSRewired-Windows-x64.zip"", ""size"": 52000000, ""browser_download_url"": ""https://github.com/patyczak232323/Rewired/releases/download/v0.5.0/DLSRewired-Windows-x64.zip"" },
    { ""name"": ""DLSRewired-Linux-x86_64.zip"", ""size"": 61000000, ""browser_download_url"": ""https://github.com/patyczak232323/Rewired/releases/download/v0.5.0/DLSRewired-Linux-x86_64.zip"" },
    { ""name"": ""DLSRewired-Android-arm64.apk"", ""size"": 40000000, ""browser_download_url"": ""https://github.com/patyczak232323/Rewired/releases/download/v0.5.0/DLSRewired-Android-arm64.apk"" }
  ]
}";

		[Test]
		public void ParsesTheLatestRelease()
		{
			ReleaseInfo release = ReleaseInfo.Parse(ReleaseJson);
			Assert.AreEqual("v0.5.0", release.Tag);
			Assert.AreEqual("0.5.0", release.Version);
			Assert.IsTrue(release.IsNewerThan("0.4.0"));
			Assert.IsFalse(release.IsNewerThan("0.5.0"));

			Assert.IsTrue(release.TryGetAsset(UpdatePlatform.Windows, out string url, out long size));
			StringAssert.EndsWith("/DLSRewired-Windows-x64.zip", url);
			Assert.AreEqual(52000000, size);
			Assert.IsTrue(release.TryGetAsset(UpdatePlatform.Android, out url, out _));
			StringAssert.EndsWith(".apk", url);
			Assert.IsFalse(release.TryGetAsset(UpdatePlatform.Other, out _, out _));
		}

		[Test]
		public void ReleaseWithoutAssetsHasNothingToDownload()
		{
			ReleaseInfo release = ReleaseInfo.Parse(@"{ ""tag_name"": ""v1.0.0"" }");
			Assert.IsFalse(release.TryGetAsset(UpdatePlatform.Linux, out _, out _));
			Assert.AreEqual(ReleaseInfo.ReleasesPageUrl, release.PageUrl);
		}

		[TestCase("0.4.1", "0.4.0", 1)]
		[TestCase("0.10.0", "0.9.9", 1)]
		[TestCase("1.0", "1.0.0", 0)]
		[TestCase("v0.4.0", "0.4.0", 0)]
		[TestCase("0.5.0-beta", "0.5.0", -1)]
		[TestCase("0.5.0-beta", "0.4.9", 1)]
		[TestCase("0.4.0", "0.5.0", -1)]
		public void ComparesVersions(string a, string b, int expected)
		{
			Assert.AreEqual(expected, System.Math.Sign(ReleaseInfo.CompareVersions(a, b)));
		}

		[Test]
		public void AssetNamesMatchTheReleaseWorkflow()
		{
			string workflow = System.IO.File.ReadAllText(System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", ".github", "workflows", "release.yml"));
			foreach (UpdatePlatform platform in new[] { UpdatePlatform.Windows, UpdatePlatform.Linux, UpdatePlatform.Android })
			{
				StringAssert.Contains(ReleaseInfo.AssetName(platform), workflow, platform.ToString());
			}
		}

		[Test]
		public void LinuxScriptWaitsCopiesAndRestarts()
		{
			string script = UpdateInstallScript.Create(UpdatePlatform.Linux, 4242, "/tmp/up/files", "/tmp/up", "/opt/Rewired it's", "DLSRewired.x86_64");
			StringAssert.StartsWith("#!/bin/sh\n", script);
			StringAssert.Contains("while kill -0 4242", script);
			StringAssert.Contains("cp -R '/tmp/up/files/.' '/opt/Rewired it'\\''s'", script);
			StringAssert.Contains("chmod +x '/opt/Rewired it'\\''s/DLSRewired.x86_64'", script);
			StringAssert.Contains("rm -rf '/tmp/up'", script);
		}

		[Test]
		public void WindowsScriptWaitsCopiesAndRestarts()
		{
			string script = UpdateInstallScript.Create(UpdatePlatform.Windows, 4242, @"C:\Temp\up\files", @"C:\Temp\up", @"C:\Games\Rewired", "DLSRewired.exe");
			StringAssert.Contains("PID eq 4242", script);
			StringAssert.Contains(@"robocopy ""C:\Temp\up\files"" ""C:\Games\Rewired"" /E", script);
			StringAssert.Contains(@"start """" ""C:\Games\Rewired\DLSRewired.exe""", script);
			StringAssert.DoesNotContain("\r\r", script);
			StringAssert.Contains("\r\n", script);
			Assert.Throws<System.ArgumentException>(() => UpdateInstallScript.Create(UpdatePlatform.Windows, 1, @"C:\%TEMP%", @"C:\x", @"C:\y", "a.exe"));
		}
	}
}
