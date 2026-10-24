using System;
using System.IO;
using UnityEngine;

namespace DLS.SaveSystem
{
	// Exports are written inside the app's own storage, which Android users cannot browse.
	// On Android 10+ this copies an export into the shared Download/Rewired folder through
	// MediaStore (no storage permission needed) and can open the system share sheet for it.
	// Elsewhere it does nothing and the export stays where it was written.
	public static class AndroidExports
	{
		public const string FolderName = "Download/Rewired";

#if UNITY_ANDROID && !UNITY_EDITOR
		static AndroidJavaObject lastUri;
		static string lastMimeType;
#endif

		public static bool CanPublish
		{
			get
			{
#if UNITY_ANDROID && !UNITY_EDITOR
				using AndroidJavaClass version = new("android.os.Build$VERSION");
				return version.GetStatic<int>("SDK_INT") >= 29;
#else
				return false;
#endif
			}
		}

		public static bool HasSharableExport
		{
			get
			{
#if UNITY_ANDROID && !UNITY_EDITOR
				return lastUri != null;
#else
				return false;
#endif
			}
		}

		// Returns true when the file was copied into Download/Rewired.
		public static bool Publish(string sourcePath, string mimeType, out string error)
		{
			error = null;
#if UNITY_ANDROID && !UNITY_EDITOR
			if (!CanPublish) return false;

			try
			{
				using AndroidJavaObject activity = CurrentActivity();
				using AndroidJavaObject resolver = activity.Call<AndroidJavaObject>("getContentResolver");
				using AndroidJavaObject values = new("android.content.ContentValues");
				values.Call("put", "_display_name", Path.GetFileName(sourcePath));
				values.Call("put", "mime_type", mimeType);
				values.Call("put", "relative_path", FolderName);

				using AndroidJavaClass downloads = new("android.provider.MediaStore$Downloads");
				using AndroidJavaObject collection = downloads.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI");
				AndroidJavaObject uri = resolver.Call<AndroidJavaObject>("insert", collection, values);
				if (uri == null)
				{
					error = "Android refused to create the file.";
					return false;
				}

				using (AndroidJavaObject stream = resolver.Call<AndroidJavaObject>("openOutputStream", uri))
				{
					byte[] bytes = File.ReadAllBytes(sourcePath);
					stream.Call("write", (sbyte[])(Array)bytes);
					stream.Call("close");
				}

				lastUri?.Dispose();
				lastUri = uri;
				lastMimeType = mimeType;
				return true;
			}
			catch (Exception e)
			{
				error = e.Message;
				return false;
			}
#else
			return false;
#endif
		}

		// Opens the share sheet for the last published export.
		public static bool ShareLast(out string error)
		{
			error = null;
#if UNITY_ANDROID && !UNITY_EDITOR
			if (lastUri == null) return false;

			try
			{
				const int FlagGrantReadUriPermission = 1;
				using AndroidJavaObject intent = new("android.content.Intent", "android.intent.action.SEND");
				intent.Call<AndroidJavaObject>("setType", lastMimeType).Dispose();
				intent.Call<AndroidJavaObject>("putExtra", "android.intent.extra.STREAM", lastUri).Dispose();
				intent.Call<AndroidJavaObject>("addFlags", FlagGrantReadUriPermission).Dispose();

				using AndroidJavaClass intentClass = new("android.content.Intent");
				using AndroidJavaObject title = new("java.lang.String", "Share export");
				using AndroidJavaObject chooser = intentClass.CallStatic<AndroidJavaObject>("createChooser", intent, title);
				using AndroidJavaObject activity = CurrentActivity();
				activity.Call("startActivity", chooser);
				return true;
			}
			catch (Exception e)
			{
				error = e.Message;
				return false;
			}
#else
			return false;
#endif
		}

#if UNITY_ANDROID && !UNITY_EDITOR
		static AndroidJavaObject CurrentActivity()
		{
			using AndroidJavaClass player = new("com.unity3d.player.UnityPlayer");
			return player.GetStatic<AndroidJavaObject>("currentActivity");
		}
#endif
	}
}
