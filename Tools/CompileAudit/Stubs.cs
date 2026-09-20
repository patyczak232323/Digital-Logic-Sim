using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }

    public static class Mathf
    {
        public static float Max(float a, float b) => Math.Max(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static float Round(float value) => (float)Math.Round(value);
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    public enum KeyCode
    {
        A='A', B='B', C='C', D='D', E='E', F='F', G='G', H='H', I='I', J='J', K='K', L='L', M='M', N='N',
        O='O', P='P', Q='Q', R='R', S='S', T='T', U='U', V='V', W='W', X='X', Y='Y', Z='Z',
        Alpha0='0', Alpha1='1', Alpha2='2', Alpha3='3', Alpha4='4', Alpha5='5', Alpha6='6', Alpha7='7', Alpha8='8', Alpha9='9'
    }

    public static class Application
    {
        public static string persistentDataPath => ".";
    }
}

namespace Seb.Helpers
{
    public static class Maths
    {
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
        public static float EaseQuadInOut(float t) => t;
    }

    public static class InputHelper
    {
        public static bool AnyKeyOrMouseHeldThisFrame => false;
        public static bool CtrlIsHeld => false;
        public static bool ShiftIsHeld => false;
        public static bool AltIsHeld => false;
        public static bool IsKeyHeld(UnityEngine.KeyCode key) => false;
    }
}

namespace DLS.Description
{
    public enum ChipType
    {
        Custom, Nand, TriStateBuffer, Clock, Pulse, dev_Ram_8Bit, Rom_256x16,
        SevenSegmentDisplay, DisplayRGB, DisplayDot, DisplayLED,
        Merge_1To4Bit, Merge_1To8Bit, Merge_4To8Bit, Split_4To1Bit, Split_8To4Bit, Split_8To1Bit,
        In_1Bit, In_4Bit, In_8Bit, Out_1Bit, Out_4Bit, Out_8Bit, Key,
        Bus_1Bit, BusTerminus_1Bit, Bus_4Bit, BusTerminus_4Bit, Bus_8Bit, BusTerminus_8Bit, Buzzer
    }

    public enum ChipCacheMode
    {
        Auto,
        Normal,
        Cached
    }

    public enum NameDisplayLocation
    {
        Centre,
        Top,
        Hidden
    }

    public enum PinBitCount
    {
        Bit1 = 1,
        Bit4 = 4,
        Bit8 = 8
    }

    public enum PinColour
    {
        Red, Orange, Yellow, Green, Blue, Violet, Pink, White
    }

    public enum PinValueDisplayMode
    {
        Off, UnsignedDecimal, SignedDecimal, HEX
    }

    public enum WireConnectionType
    {
        ToPins,
        ToWireSource,
        ToWireTarget
    }

    public static class ChipTypeHelper
    {
        public static bool IsBusOriginType(ChipType type) => type is ChipType.Bus_1Bit or ChipType.Bus_4Bit or ChipType.Bus_8Bit;
        public static bool IsBusTerminusType(ChipType type) => type is ChipType.BusTerminus_1Bit or ChipType.BusTerminus_4Bit or ChipType.BusTerminus_8Bit;
    }

    public struct PinAddress
    {
        public int PinID;
        public int PinOwnerID;
        public PinAddress(int pinOwnerID, int pinID) { PinOwnerID = pinOwnerID; PinID = pinID; }
    }

    public struct PinDescription
    {
        public string Name;
        public int ID;
        public UnityEngine.Vector2 Position;
        public PinBitCount BitCount;
        public PinColour Colour;
        public PinValueDisplayMode ValueDisplayMode;

        public PinDescription(string name, int id, UnityEngine.Vector2 position, PinBitCount bitCount, PinColour colour, PinValueDisplayMode valueDisplayMode)
        {
            Name = name;
            ID = id;
            Position = position;
            BitCount = bitCount;
            Colour = colour;
            ValueDisplayMode = valueDisplayMode;
        }
    }

    public struct OutputPinColourInfo
    {
        public PinColour PinColour;
        public int PinID;
        public OutputPinColourInfo(PinColour pinColour, int pinID) { PinColour = pinColour; PinID = pinID; }
    }

    public struct SubChipDescription
    {
        public string Name;
        public int ID;
        public string Label;
        public UnityEngine.Vector2 Position;
        public OutputPinColourInfo[] OutputPinColourInfo;
        public uint[] InternalData;
        public bool MirrorX;
        public bool MirrorY;

        public SubChipDescription(string name, int id, string label, UnityEngine.Vector2 position, OutputPinColourInfo[] outputPinColInfo, uint[] internalData = null, bool mirrorX = false, bool mirrorY = false)
        {
            Name = name;
            ID = id;
            Label = label;
            Position = position;
            OutputPinColourInfo = outputPinColInfo;
            InternalData = internalData;
            MirrorX = mirrorX;
            MirrorY = mirrorY;
        }
    }

    public struct WireDescription
    {
        public PinAddress SourcePinAddress;
        public PinAddress TargetPinAddress;
        public WireConnectionType ConnectionType;
        public int ConnectedWireIndex;
        public int ConnectedWireSegmentIndex;
        public UnityEngine.Vector2[] Points;
    }
    public struct DisplayDescription { }

    public class ChipDescription
    {
        public const StringComparison NameComparison = StringComparison.OrdinalIgnoreCase;
        public static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
        public string DLSVersion;
        public string Name;
        public NameDisplayLocation NameLocation;
        public ChipType ChipType;
        public ChipCacheMode CacheMode;
        public UnityEngine.Vector2 Size;
        public UnityEngine.Color Colour;
        public PinDescription[] InputPins = Array.Empty<PinDescription>();
        public PinDescription[] OutputPins = Array.Empty<PinDescription>();
        public SubChipDescription[] SubChips = Array.Empty<SubChipDescription>();
        public WireDescription[] Wires = Array.Empty<WireDescription>();
        public DisplayDescription[] Displays = Array.Empty<DisplayDescription>();

        public bool NameMatch(string otherName) => NameMatch(Name, otherName);
        public static bool NameMatch(string a, string b) => string.Equals(a, b, NameComparison);
    }
}

namespace DLS.Game
{
    using DLS.Description;

    public class PinInstance
    {
        public PinAddress Address;
        public uint PlayerInputState;
        public uint State;
    }

    public class DevPinInstance
    {
        public PinInstance Pin = new PinInstance();
    }

    public class ChipLibrary
    {
        readonly Dictionary<string, ChipDescription> descriptions = new(ChipDescription.NameComparer);
        public readonly List<ChipDescription> allChips = new();

        public ChipLibrary() { }

        public ChipLibrary(params ChipDescription[] chipDescriptions)
        {
            if (chipDescriptions == null) return;
            for (int i = 0; i < chipDescriptions.Length; i++)
            {
                ChipDescription description = chipDescriptions[i];
                if (description == null || string.IsNullOrWhiteSpace(description.Name)) continue;
                descriptions[description.Name] = description;
                allChips.Add(description);
            }
        }

        public ChipDescription GetChipDescription(string name)
        {
            if (descriptions.TryGetValue(name ?? string.Empty, out ChipDescription description)) return description;
            return new ChipDescription { Name = name };
        }

        public bool TryGetChipDescription(string name, out ChipDescription description)
        {
            if (descriptions.Count == 0)
            {
                description = GetChipDescription(name);
                return description != null;
            }

            return descriptions.TryGetValue(name ?? string.Empty, out description);
        }

        public ChipDescription[] GetDirectParentChips(string chipName) => Array.Empty<ChipDescription>();
    }

    public static class Main
    {
        public static object DLSVersion => "test";
    }

    public static class SubChipInstance
    {
        public static UnityEngine.Vector2 CalculateMinChipSize(PinDescription[] inputPins, PinDescription[] outputPins, string name)
            => new UnityEngine.Vector2(1.5f, 1.0f);
    }

    public class ProjectDescription
    {
        public string ProjectName;
    }

    public class Project
    {
        public static Project ActiveProject;
        public ProjectDescription description = new ProjectDescription();
    }
}

namespace DLS.SaveSystem
{
    public static class SavePaths
    {
        public static string GetProjectPath(string projectName) => projectName ?? string.Empty;
    }
}


namespace DLS.Graphics
{
    public static class DrawSettings
    {
        public const float GridSize = 0.125f;
    }
}
