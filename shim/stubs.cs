using System.Reflection;
[assembly: AssemblyVersion("10.9.0.0")]
[assembly: AssemblyFileVersion("10.9.0.0")]

namespace Microsoft.VisualBasic.ApplicationServices
{
    // Base type for VB's auto-generated My.MyApplication. Present only so the
    // type loads during DWSIM's reflection scan; headless runs never use it.
    public class ApplicationBase
    {
        public ApplicationBase() { }
    }
}

namespace Microsoft.VisualBasic.Devices
{
    // Base type for VB's auto-generated My.MyComputer, same rationale.
    public class Computer
    {
        public Computer() { }
    }
}
