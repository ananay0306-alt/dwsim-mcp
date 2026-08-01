using System; using System.IO; using System.Linq; using System.Reflection; using System.Text;
class GenFwd {
    static string Name(Type t) {
        var n = t.FullName.Replace('+','.');
        if (t.IsGenericTypeDefinition) {
            int argc = t.GetGenericArguments().Length;
            n = n.Substring(0, n.IndexOf('`')) + "<" + new string(',', argc-1) + ">";
        }
        return n;
    }
    static void Main() {
        var asm = Assembly.Load("Microsoft.VisualBasic.Core");
        var sb = new StringBuilder();
        int n = 0;
        foreach (var t in asm.GetExportedTypes().OrderBy(x => x.FullName)) {
            if (t.IsNested) continue;
            var fn = t.FullName;
            if (fn == "Microsoft.VisualBasic.ApplicationServices.ApplicationBase") continue;
            if (fn == "Microsoft.VisualBasic.Devices.Computer") continue;
            sb.AppendLine("[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(" + Name(t) + "))]");
            n++;
        }
        File.WriteAllText("forwards.cs", sb.ToString());
        Console.WriteLine("forwards generated: " + n);
    }
}
