using System.Runtime.InteropServices;
using UnityEngine;

namespace DLS.SaveSystem
{
	// On WebGL, saved files live in memory until they are flushed to IndexedDB. Saves mark
	// the storage dirty and Update flushes it shortly after; a periodic flush catches any
	// file operation that did not go through Saver. Does nothing on other platforms.
	public static class WebStorage
	{
		const float FlushDelaySeconds = 0.5f;
		const float PeriodicFlushSeconds = 5f;

		static bool dirty;
		static float dirtySince;
		static float lastFlush;

#if UNITY_WEBGL && !UNITY_EDITOR
		[DllImport("__Internal")]
		static extern void RewiredSyncFS();
#endif

		public static void MarkDirty()
		{
			if (!dirty) dirtySince = Time.unscaledTime;
			dirty = true;
		}

		public static void Update()
		{
#if UNITY_WEBGL && !UNITY_EDITOR
			float now = Time.unscaledTime;
			bool flushSave = dirty && now - dirtySince >= FlushDelaySeconds;
			if (!flushSave && now - lastFlush < PeriodicFlushSeconds) return;

			dirty = false;
			lastFlush = now;
			RewiredSyncFS();
#endif
		}
	}
}
