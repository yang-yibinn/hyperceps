using System;
using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel;

namespace Cordyceps
{
    /// <summary>
    /// Hyperceps plugin identity. Its own name and GUID let it load alongside the original
    /// Cordyceps (by Brooks Talley, MIT), which this plugin is built from.
    /// </summary>
    public class CordycepsInfo : GH_AssemblyInfo
    {
        public override string Name => "Hyperceps";

        public override Bitmap Icon => null;

        public override string Description => "Lets Claude build and edit Grasshopper definitions in a few validated calls: batch edits, a compact canvas outline, data-tree checks and slider sweeps. Use it with the Hyperceps skill for Claude. Based on Cordyceps by Brooks Talley (github.com/brookstalley/cordyceps)";

        public override Guid Id => new Guid("81f40476-2a54-4055-9856-a7976c4395fd");

        public override string AuthorName => "Yibin Yang; based on Cordyceps by Brooks Talley";

        public override string AuthorContact => "https://github.com/yang-yibinn/hyperceps";

        public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();
    }
}
