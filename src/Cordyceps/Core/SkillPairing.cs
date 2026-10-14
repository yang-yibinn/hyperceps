using System;

namespace Cordyceps.Core
{
    /// <summary>
    /// Hyperceps is two parts, the Grasshopper plugin and the Claude skill <c>hyperceps</c>, released
    /// together under one version number. The connection note opens with the plugin's version and asks
    /// the agent to load the skill, or, when it isn't installed, to tell the user once how to get it.
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

        /// <summary>The first lines of the connection note: name and version, then the skill.</summary>
        public static string Header(string version)
        {
            var v = ReleaseVersion(version);
            return $"Hyperceps {v}: MCP control of Grasshopper (parametric 3D design). Built from Cordyceps by Brooks Talley." + Environment.NewLine
                + Environment.NewLine
                + $"SKILL: Hyperceps is made to be used with its Claude skill \"{SkillName}\" (same version, {v})." + Environment.NewLine
                + $"- If your app supports skills and \"{SkillName}\" isn't loaded yet, load it now, before working on the canvas." + Environment.NewLine
                + "- If it isn't installed, tell the user once, then carry on: \"Hyperceps works best with its Claude skill: download" + Environment.NewLine
                + $"  {SkillZip} from {ReleasesUrl} and copy the {SkillName} folder into ~/.claude/skills (Claude Code)," + Environment.NewLine
                + "  or upload the zip under Skills in Claude's settings.\"" + Environment.NewLine
                + "- If the skill names a different version, tell the user to update the older part." + Environment.NewLine
                + Environment.NewLine;
        }
    }
}
