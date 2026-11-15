using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AltTabEverywhere.Interop;

namespace AltTabEverywhere.Tests;

public class InteropTests
{
    /// <summary>
    /// Any native call that takes text must marshal it as UTF-16. The default (ANSI) hands the API
    /// a buffer half the size it's told, which corrupts the heap and kills the app (v0.5 typing crash).
    /// </summary>
    [Fact]
    public void Every_native_call_that_takes_text_marshals_it_as_unicode()
    {
        var offenders = typeof(Native).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Select(m => (Method: m, Import: m.GetCustomAttribute<DllImportAttribute>()))
            .Where(x => x.Import != null && x.Import.CharSet != CharSet.Unicode)
            .Where(x => x.Method.GetParameters().Any(p => TakesText(p)))
            .Select(x => $"{x.Method.DeclaringType!.Name}.{x.Method.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    private static bool TakesText(ParameterInfo p)
    {
        if (p.GetCustomAttribute<MarshalAsAttribute>() != null) return false; // marshalling stated explicitly
        var type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
        return type == typeof(string) || type == typeof(StringBuilder) || type == typeof(char[]) || type == typeof(char);
    }

    [Fact]
    public void ToUnicodeEx_types_letters_without_harming_the_heap()
    {
        var state = new byte[256];
        var chars = new char[8];
        IntPtr layout = Native.GetKeyboardLayout(0);
        // Enough calls that a heap overrun would surface as a crash or a corrupted result.
        for (int i = 0; i < 2000; i++)
        {
            int count = Native.ToUnicodeEx('A', 0x1E, state, chars, chars.Length, Native.TOUNICODE_NO_STATE_CHANGE, layout);
            Assert.True(count >= 1);
            Assert.False(char.IsControl(chars[0]));
        }
    }
}
