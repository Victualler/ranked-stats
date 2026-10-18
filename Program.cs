using System;
using System.Linq;
using System.Reflection;
using SteamKit2.GC.Dota.Internal;

class Program {
    static void Main() {
        DumpType(typeof(CMsgDOTAMatch));
        DumpType(typeof(CMsgDOTAMatch.Player));
        DumpType(typeof(CMsgDOTAProfileCard));
        DumpType(typeof(CMsgDOTAProfileCard.Slot));
    }

    static void DumpType(Type type) {
        Console.WriteLine($"\n=== {type.Name} Properties ===");
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Select(p => $"{p.PropertyType.Name} {p.Name}");
        foreach (var p in props) {
            Console.WriteLine($"  {p}");
        }
    }
}
