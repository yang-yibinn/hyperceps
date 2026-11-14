using System;

namespace Cordyceps.Core
{
    /// <summary>
    /// Hyperceps is two parts, the Grasshopper plugin and the Claude skill <c>hyperceps</c>, released
    /// together under one version number. The connection note opens with the plugin's version and asks
    /// the agent to load the skill, or, when it isn't installed, to read the plugin's own copy of it.
    /// The skill names the version it is for (<see cref="SkillVersionLine"/>); a unit test keeps that
    /// line and the plugin's version the same.
    /// </summary>
    public static class SkillPairing
    {
        public const string SkillName = "hyperceps";
        public const string ReleasesUrl = "https://github.com/yang-yibinn/hyperceps/releases";
        public const string SkillZip = "hyperceps-skill.zip";

        /// <summary>The start of the line in SKILL.md that names the plugin version the skill is for.</summary>
        public const string SkillVersionLine = "This skill is for Hyperceps ";

        /// <summary>The release number without the build stamp: "1.0.0+build20261014..." gives "1.0.0".</summary>
        public static string ReleaseVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return BuildVersion.Unknown;
            var v = version.Trim();
            int plus = v.IndexOf('+');
            return plus > 0 ? v.Substring(0, plus) : v;
        }

        /// <summary>
        /// The first lines of the connection note: name and version, then the guide. Since 1.1 the plugin
        /// serves the skill itself (<see cref="SkillText"/>), so an app without the installed skill reads it
        /// with <see cref="SkillText.Call"/> and nobody has to install anything.
        /// </summary>
        public static string Header(string version)
        {
            var v = ReleaseVersion(version);
            return $"Hyperceps {v}: MCP control of Grasshopper (parametric 3D design). Built from Cordyceps by Brooks Talley." + Environment.NewLine
                + Environment.NewLine
                + $"GUIDE FIRST: before working on the canvas, read the Hyperceps guide once (the \"{SkillName}\" skill, version {v}):" + Environment.NewLine
                + $"- If your app lists a \"{SkillName}\" skill, load it." + Environment.NewLine
                + $"- Otherwise call {SkillText.Call}: the plugin returns the same guide. Nothing to install." + Environment.NewLine
                + $"- If a loaded skill names another version, call {SkillText.Call} instead and tell the user" + Environment.NewLine
                + "  once that their installed skill doesn't match the plugin (update or remove it)." + Environment.NewLine
                + Environment.NewLine;
        }
    }
}
