using UnityEngine;

namespace Seb.Helpers.InputHandling
{
	// Touches for the touch interface: the device's own on a phone, or the left mouse button
	// acting as one finger in the "-mobile-ui" desktop preview, so the touch code (taps,
	// drags, long presses) can be tried and screenshotted without a phone.
	public static class TouchInput
	{
		public static bool EmulateWithMouse;

		static int emulatedFrame = -1;
		static bool emulatedActive;
		static bool endPending;
		static Touch emulatedTouch;
		static Vector2 lastMousePos;

		public static int Count
		{
			get
			{
				if (!EmulateWithMouse) return Input.touchCount;
				UpdateEmulatedTouch();
				return emulatedActive ? 1 : 0;
			}
		}

		public static Touch GetTouch(int index)
		{
			if (!EmulateWithMouse) return Input.GetTouch(index);
			UpdateEmulatedTouch();
			return emulatedTouch;
		}

		static void UpdateEmulatedTouch()
		{
			if (emulatedFrame == Time.frameCount) return;
			emulatedFrame = Time.frameCount;

			Vector2 pos = Input.mousePosition;
			bool down = Input.GetMouseButtonDown(0);
			bool up = Input.GetMouseButtonUp(0);
			bool held = Input.GetMouseButton(0);
			TouchPhase phase;

			if (endPending)
			{
				// A click whose press and release arrived in the same frame: a finger reports
				// Began and Ended on separate frames, so the release follows here.
				endPending = false;
				phase = TouchPhase.Ended;
			}
			else if (down)
			{
				endPending = up && !held;
				phase = TouchPhase.Began;
			}
			else if (up)
			{
				phase = TouchPhase.Ended;
			}
			else if (held)
			{
				phase = (pos - lastMousePos).sqrMagnitude > 0.01f ? TouchPhase.Moved : TouchPhase.Stationary;
			}
			else
			{
				emulatedActive = false;
				lastMousePos = pos;
				return;
			}

			emulatedActive = true;
			emulatedTouch = new Touch
			{
				fingerId = 0,
				position = pos,
				deltaPosition = pos - lastMousePos,
				phase = phase,
				tapCount = 1,
				type = TouchType.Direct
			};
			lastMousePos = pos;
		}
	}
}
