// Minimal stand-ins for the Unity and editor types that the engine code references.
// Only what the compiled engine files need; keep behaviour trivial and deterministic.
using System;
namespace UnityEngine
{
	public struct Vector2
	{
		public float x, y;
		public Vector2(float x, float y) { this.x = x; this.y = y; }
		public static Vector2 zero => new(0, 0);
		public static Vector2 one => new(1, 1);
		public static Vector2 up => new(0, 1);
		public static Vector2 down => new(0, -1);
		public static Vector2 left => new(-1, 0);
		public static Vector2 right => new(1, 0);
		public static Vector2 operator +(Vector2 a, Vector2 b) => new(a.x + b.x, a.y + b.y);
		public static Vector2 operator -(Vector2 a, Vector2 b) => new(a.x - b.x, a.y - b.y);
		public static Vector2 operator -(Vector2 a) => new(-a.x, -a.y);
		public static Vector2 operator *(Vector2 a, float s) => new(a.x * s, a.y * s);
		public static Vector2 operator *(float s, Vector2 a) => new(a.x * s, a.y * s);
		public static Vector2 operator /(Vector2 a, float s) => new(a.x / s, a.y / s);
		public static bool operator ==(Vector2 a, Vector2 b) => a.x == b.x && a.y == b.y;
		public static bool operator !=(Vector2 a, Vector2 b) => !(a == b);
		public override bool Equals(object o) => o is Vector2 v && v == this;
		public override int GetHashCode() => HashCode.Combine(x, y);
		public static float Distance(Vector2 a, Vector2 b) => MathF.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
		public float magnitude => MathF.Sqrt(x * x + y * y);
		public float sqrMagnitude => x * x + y * y;
	}
	public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
	public struct Color
	{
		public float r, g, b, a;
		public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
		public static Color white => new(1, 1, 1);
		public static Color black => new(0, 0, 0);
		public static Color red => new(1, 0, 0);
		public static Color green => new(0, 1, 0);
		public static Color yellow => new(1, 0.92f, 0.016f);
		public static Color clear => new(0, 0, 0, 0);
	}
	public static class Mathf
	{
		public static float Max(float a, float b) => Math.Max(a, b);
		public static int Max(int a, int b) => Math.Max(a, b);
		public static float Min(float a, float b) => Math.Min(a, b);
		public static int Min(int a, int b) => Math.Min(a, b);
		public static float Clamp(float v, float a, float b) => Math.Clamp(v, a, b);
		public static int Clamp(int v, int a, int b) => Math.Clamp(v, a, b);
		public static float Clamp01(float v) => Math.Clamp(v, 0, 1);
		public static int RoundToInt(float v) => (int)MathF.Round(v);
		public static int CeilToInt(float v) => (int)MathF.Ceiling(v);
		public static int FloorToInt(float v) => (int)MathF.Floor(v);
		public static float Round(float v) => MathF.Round(v);
		public static float Abs(float v) => MathF.Abs(v);
		public static float Sqrt(float v) => MathF.Sqrt(v);
		public static float Log(float v, float b) => MathF.Log(v, b);
		public static float Pow(float a, float b) => MathF.Pow(a, b);
		public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
		public const float PI = MathF.PI;
	}
	public static class Debug
	{
		public static void Log(object o) => Console.WriteLine(o);
		public static void LogWarning(object o) => Console.WriteLine("WARN " + o);
		public static void LogError(object o) => Console.WriteLine("ERROR " + o);
		public static void LogException(Exception e) => Console.WriteLine("EXC " + e);
	}
	public enum RuntimePlatform { WindowsPlayer, LinuxPlayer, Android, OSXPlayer, WindowsEditor, LinuxEditor }
	public static class Application
	{
		public static string dataPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Assets"));
		public static string persistentDataPath = System.IO.Path.GetTempPath();
		public static bool isEditor => true;
		public static bool isMobilePlatform => false;
		public static RuntimePlatform platform => RuntimePlatform.LinuxEditor;
	}
	public static class Time { public static float unscaledTime => (float)Environment.TickCount64 / 1000f; public static float unscaledDeltaTime => 0.016f; public static int frameCount => 0; }
	public enum FullScreenMode { ExclusiveFullScreen, FullScreenWindow, MaximizedWindow, Windowed }
	public enum KeyCode
	{
		None, A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
		Alpha0, Alpha1, Alpha2, Alpha3, Alpha4, Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,
		Escape, Return, Space, Backspace, Delete, Tab, LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt,
		LeftCommand, RightCommand, UpArrow, DownArrow, LeftArrow, RightArrow, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
		Minus, Equals, Plus, KeypadEnter, Home, End, PageUp, PageDown, Insert, Slash, Backslash, Period, Comma, Semicolon, Quote,
		LeftBracket, RightBracket, BackQuote, Mouse0, Mouse1, Mouse2
	}
}
namespace Seb.Helpers
{
	public static class InputHelper
	{
		public static bool AnyKeyOrMouseHeldThisFrame => false;
		public static bool CtrlIsHeld => false;
		public static bool ShiftIsHeld => false;
		public static bool AltIsHeld => false;
		public static bool IsKeyHeld(UnityEngine.KeyCode k) => false;
	}
	public static class Maths { public static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0, 1); public static float EaseQuadInOut(float t) => t; }
}
namespace DLS.Graphics
{
	public static class DrawSettings
	{
		public const float GridSize = 0.125f;
		public const float ChipOutlineWidth = 0.05f;
		public const float SubChipPinInset = 0.1f;
		public static float CalculateGridSnappedWidth(float w) => w;
	}
}
namespace DLS.Game
{
	using DLS.Description;
	using DLS.Simulation;
	public class Project { public static Project ActiveProject; public ProjectDescription description; public ChipLibrary chipLibrary; public Project(ProjectDescription d, ChipLibrary l) { description = d; chipLibrary = l; } }
	public static class GridHelper { public static float SnapToGrid(float v) => v; public static float SnapToGridForceEven(float v) => v; }
	public class SubChipInstance
	{
		public static float MinChipHeightForPins(PinDescription[] a, PinDescription[] b) => 1f;
		public static UnityEngine.Vector2 CalculateMinChipSize(PinDescription[] a, PinDescription[] b, string n) => new(2, 2);
	}
	public static class Main { public static readonly System.Version DLSVersion = new(2, 1, 6); public static readonly System.Version DLSVersion_EarliestCompatible = new(2, 0, 0); }
	public class PinInstance { public uint State; public uint PlayerInputState; public PinAddress Address; public int ID; }
	public class DevPinInstance
	{
		public PinInstance Pin; public string Name; public int ID; public PinDescription Description;
		public DevPinInstance(PinDescription d, bool isInput) { Description = d; Name = d.Name; ID = d.ID; Pin = new PinInstance { Address = new PinAddress(d.ID, 0), ID = d.ID }; }
	}
}
namespace DLS.SaveSystem
{
	public static class UpgradeHelper { public static void ApplyVersionChanges(DLS.Description.ChipDescription[] a, DLS.Description.ChipDescription[] b) { } }
}
