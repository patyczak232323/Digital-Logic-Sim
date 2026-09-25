#if UNITY_EDITOR
using System;
using DLS.Simulation;
using UnityEngine;

namespace DLS.EditorTools
{
	public static class CompatibilitySelfTestCommand
	{
		public static void Run()
		{
			CompatibilitySuiteResult result = CompatibilitySelfTestSuite.RunAll();

			for (int i = 0; i < result.Cases.Length; i++)
			{
				CompatibilityCaseResult testCase = result.Cases[i];
				string line = $"[COMPAT] {(testCase.Passed ? "PASS" : "FAIL")}  {testCase.Name} — {testCase.Details}";
				if (testCase.Passed) Debug.Log(line);
				else Debug.LogError(line);
			}

			Debug.Log($"[COMPAT] {result.PassedCount}/{result.Cases.Length} compatibility cases passed.");

			if (!result.Passed)
			{
				throw new Exception(
					$"Rewired compatibility self-test failed: {result.FailedCount} of {result.Cases.Length} cases failed.");
			}
		}
	}
}
#endif
