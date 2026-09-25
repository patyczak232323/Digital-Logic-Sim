#if UNITY_EDITOR
using System;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace DLS.EditorTools
{
	public sealed class CompatibilityBuildPreprocessor : IPreprocessBuildWithReport
	{
		public int callbackOrder => -1000;

		public void OnPreprocessBuild(BuildReport report)
		{
			if (!string.Equals(
				    Environment.GetEnvironmentVariable("REWIRED_RUN_COMPATIBILITY"),
				    "1",
				    StringComparison.Ordinal))
			{
				return;
			}

			CompatibilitySelfTestCommand.Run();
		}
	}
}
#endif
