namespace Cordyceps.Core
{
    /// <summary>
    /// The connection note: the <c>instructions</c> an MCP client receives on initialize.
    ///
    /// <para>Claude Code shows only the first <see cref="ClaudeCodeLimit"/> characters of it and cuts
    /// the rest ("… [truncated]"). Measured on Nov 14, 2026: the 1.0.0 note was 7,473 characters and was
    /// cut inside its tool list, so its second half (status block, busy guidance, key points) never
    /// reached Claude Code agents. The note is therefore kept under <see cref="MaxLength"/>, essentials
    /// first, and a unit test checks it. The details live in the guide (the skill, which the plugin also
    /// serves with <c>gh_inspect(action='skill')</c>), the tools' descriptions and help, and the
    /// <c>gh://docs</c> guides.</para>
    /// </summary>
    public static class ServerNote
    {
        /// <summary>The characters of the note Claude Code shows (measured; see the class summary).</summary>
        public const int ClaudeCodeLimit = 4096;

        /// <summary>The longest the note may be, counting Windows line endings, with room for a long version.</summary>
        public const int MaxLength = 3900;

        /// <summary>The whole note for the given plugin version.</summary>
        public static string Text(string version) => SkillPairing.Header(version) + Body;

        /// <summary>The note after its header (<see cref="SkillPairing.Header"/>).</summary>
        public const string Body = @"WORKING METHOD (every tool call is a full round trip, so fewer, larger calls are much cheaper):
1. Read the canvas once with gh_canvas(action='outline'): each group, what flows in and out of it,
   sliders and errors, in one short call. Then group='NAME' for how one group is wired inside
   (group='network 1' for a whole network on a canvas without groups, group='ungrouped' for objects
   outside groups), or id='NAME' for what is around one object. Then plan.
2. For two or more changes, use ONE gh_batch call; single-step tools are for a single change.
3. Build large work one block per batch (typically 20-80 objects) and check each block before the next:
   options={""watch"": [""BLOCK OUTPUT""]} returns its item counts in the same call.
4. Graft, flatten, simplify and reverse are port settings (inputModifiers / outputModifiers), not
   Graft Tree / Flatten Tree / Simplify Tree / Reverse List components. A list of values is one panel,
   one value per line - not a comma list plus Text Split.
5. Refer to existing objects by nickname (or GUID prefix) and reuse refs from earlier batches. These
   names work as the id in EVERY tool (gh_script, gh_inspect, gh_canvas, gh_wire), not only gh_batch.
6. After a change that affects tree structure, check those outputs with gh_inspect(action='tree').
7. Send the real gh_batch: a batch with errors changes nothing and lists them all, so a dry run of the
   whole block only means writing it twice. Use options.dryRun to try a few guessed names first.
8. To check results across slider (or value list, toggle) values, use ONE
   gh_document(action='sweep', id='SLIDER', values='[5,7,9]', watch='[""OUTPUT"", ""obj.port""]'): it sets
   each value, solves, reports item counts and errors, and puts the control back.
NAMING: never rename native components. Give capitalised nicknames to the parameters at a block's
edges and to input panels/sliders (""OFFSET (m)""); name script components after their function;
put each block in a group named in capitals after what it makes; refs are lowercase.
LICHEN THALLI: groups that outline shows as THALLUS carry the author's description and properties;
read them to understand a block's purpose. Hyperceps never changes a Thallus (its members, name,
colour or hidden T output); the user does that in Grasshopper with Lichen.
SCRIPTS: gh_script set/configure rebuild the component; add solve=true to get its results and errors in
the same call. To change part of a script, edit with only the changed passages
(edits='[{""old"": ""..."", ""new"": ""...""}]'), never the whole script again.
BUSY IS NOT DEAD: a call that finds Grasshopper busy waits up to 15 s, then says what is solving and
does nothing. If a call is slow or silent, call gh_inspect(action='connection'), which always answers:
solving=true -> wait and retry; modal_inferred=true -> a dialog is open in Rhino that only the user can
close, so stop and tell them. Each result's ""status"" names the .gh file it acted on (the focused tab).
CLUSTERS: in a cluster editor never suggest F5; gh_document(action='recompute') is cluster-safe.

Each tool lists its actions in its description; action='help' gives details.
Resources: gh://skill (the guide), gh://docs/getting-started, gh://docs/common-errors, gh://component/{name}
";
    }
}
