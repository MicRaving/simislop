/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FSO.SimAntics;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The lot has to come out of a failed architecture simulation the way it went in.
    ///
    /// <para>
    /// <b>The failure.</b> <c>VMArchitecture.SimulateCommands</c> answers a placement by swapping the
    /// lot's <em>real</em> wall, floor and height planes for the preview ones and clearing
    /// <c>RealMode</c>, running the command list against them, and swapping them back. With the swap
    /// back written as a tail statement rather than a <c>finally</c>, a <c>RunCommands</c> that
    /// throws - which it does for anything it has no placement model for: a tile outside the
    /// buildable area, a contained-object placement, a modded object - left all four swapped and
    /// <c>RealMode</c> false for the rest of the session.
    /// </para>
    ///
    /// <para>
    /// <b>What that looks like to a player, and why it is only reachable with a flag on.</b> A lot
    /// whose architecture is left bound to the preview planes draws the build grid from the preview
    /// copy, commits every placement into arrays <c>WorldUI.Walls</c> / <c>WorldUI.Floors</c> never
    /// read, and leaves objects it was serving occupied - which is what makes the pie menu refuse
    /// every interaction on them. Nothing recovers it but a reload. It is only reachable with
    /// <c>NeighbourhoodWorldServices</c> on because
    /// <see cref="SimitoneGlobalLink.Tick"/> <em>catches</em> that throw and drops the command,
    /// which turns a crash into a silent permanent wedge; with the flag off
    /// <c>VMTS1GlobalLinkStub.Tick</c> lets it escape, so the same placement kills the frame instead
    /// of the lot.
    /// </para>
    ///
    /// <para>
    /// <b>Why this file reads source.</b> A <c>VMArchitecture</c> needs
    /// <c>Content.Content.Get()</c>, so it cannot be built here - the same constraint
    /// <c>SimitoneSurroundingsLiveLotIsolationTests</c> documents, and the reason this repository
    /// pins engine behaviour it cannot run by reading it with the comments stripped. What
    /// <em>can</em> be run for real is the clause the link adds to its warning line, which is the
    /// part that turns an hour of guessing into one line.
    /// </para>
    /// </summary>
    public sealed class VMArchitectureSimulationTests
    {
        // ---- the engine: the unwind is unconditional --------------------------------------------------

        [Fact]
        public void ASimulationThatThrowsStillPutsTheLotBackTheWayItFoundIt()
        {
            var body = MethodBody(SimAntics("VMArchitecture.cs"),
                "public int SimulateCommands(List<VMArchitectureCommand> commands, bool visualChange)");

            //The four things that are swapped on the way in, each of which has to be restored.
            foreach (var swap in new[]
            {
                "RealMode = false;",
                "Terrain.EnterVis();",
                "Floors = VisFloors;",
                "Walls = VisWalls;"
            })
            {
                Assert.Contains(swap, body);
            }

            //The restore is in a finally, not after the try. A restore written as a tail statement
            //is the whole bug: it is reached on the success path and skipped on every other one.
            var tryAt = body.IndexOf("try", StringComparison.Ordinal);
            var finallyAt = body.IndexOf("finally", StringComparison.Ordinal);
            Assert.True(tryAt > 0, "SimulateCommands no longer guards the swap");
            Assert.True(finallyAt > tryAt, "the restore is not in a finally");

            //and the finally holds every one of the four, in the order the swap took them.
            var unwind = body.Substring(finallyAt);
            foreach (var restore in new[]
            {
                "Floors = oldFloors;",
                "Walls = oldWalls;",
                "WallsAt = oldWallsAt;",
                "Terrain.ExitVis();",
                "RealMode = true;"
            })
            {
                Assert.Contains(restore, unwind);
            }
        }

        [Fact]
        public void RunCommandsIsTheOnlyStatementInsideTheGuard()
        {
            //Which is what makes the guard cheap as well as correct: the expensive thing - a whole
            //placement simulation - is inside, and the four restores are in the handler that runs
            //whatever happens to it.
            var body = MethodBody(SimAntics("VMArchitecture.cs"),
                "public int SimulateCommands(List<VMArchitectureCommand> commands, bool visualChange)");

            var run = body.IndexOf("cost = RunCommands(commands, visualChange);", StringComparison.Ordinal);
            var finallyAt = body.IndexOf("finally", StringComparison.Ordinal);
            Assert.True(run > 0 && run < finallyAt,
                "RunCommands is no longer inside the guarded region");

            //and the terrain's dirty bit is only cleared on the path that got there, so a simulation
            //that threw leaves the terrain asking to be redrawn rather than trusting a preview.
            var clear = body.IndexOf("if (!visualChange) TerrainDirty = false;", StringComparison.Ordinal);
            Assert.True(clear > run && clear < finallyAt,
                "TerrainDirty is cleared outside the guarded region");
        }

        [Fact]
        public void TheEngineItselfDerivesTheLotsSunFromTheLotsOwnClockEveryTick()
        {
            //The other half of the same audit, and the reason the screen stopped calling
            //SetTimeOfDay on a live lot: the engine already owns that state, and writes it from the
            //VM's own tick. A second writer from outside the tick is not a second sun, it is a race.
            var body = MethodBody(SimAntics("VMArchitecture.cs"), "public void Tick()");
            var own = body.IndexOf("SetTimeOfDay();", StringComparison.Ordinal);
            Assert.True(own > 0, "VMArchitecture.Tick no longer calls SetTimeOfDay()");

            //and it is on the unconditional tail, after every dirty-region branch, not behind one.
            var redraw = body.IndexOf("if (VM.UseWorld && Redraw)", StringComparison.Ordinal);
            Assert.True(own > redraw,
                "SetTimeOfDay() moved behind a branch, so a lot in preview would keep the last sun");
        }

        // ---- the link: the tripwire --------------------------------------------------------------------

        [Fact]
        public void AFailedSimulationSaysWhetherTheLotCameBack()
        {
            var source = CommentsRemoved(ClientSource("Neighbourhood", "SimitoneGlobalLink.cs"));
            var body = MethodBody(source, "public void Tick(VM vm)");

            //The command is dropped rather than verified - a placement the lot cannot answer costs
            //the player that placement and nothing else, which is only safe because of the finally
            //the two files above now agree on.
            Assert.Contains("catch (Exception e)", body);
            Assert.Contains("could not simulate a queued", body);
            Assert.Contains("DescribeSimulationState(vm)", body);
            Assert.Contains("continue;", body);

            //and the clause it appends is the architecture's own RealMode, read rather than assumed.
            Assert.Contains("architecture.RealMode", source);
        }

        [Fact]
        public void TheClauseNamesTheWedgeAndIsSilentWhenThereIsNone()
        {
            //A healthy lot adds nothing to the line; a wedged one says the sentence that turns an
            //hour of bug-hunting into one log line.
            Assert.Equal(string.Empty, SimitoneGlobalLink.DescribeSimulationState(true));

            var wedged = SimitoneGlobalLink.DescribeSimulationState(false);
            Assert.Contains("still in its simulation preview", wedged);
            Assert.Contains("build/buy", wedged);
            Assert.Contains("placement", wedged);

            //the rule itself: out of preview, or not
            Assert.True(SimitoneGlobalLink.LotIsOutOfSimulationPreview(true));
            Assert.False(SimitoneGlobalLink.LotIsOutOfSimulationPreview(false));
        }

        [Fact]
        public void TheLinkStillVerifiesAndForwardsExactlyWhatTheStubDid()
        {
            //B5's own rule - "keep the local verify/forward semantics" - checked against the stub
            //rather than against a description of it. The drain condition, the free command, the
            //charge, and the forward on success are the four things that decide whether a placed
            //object applies at all.
            var link = CommentsRemoved(ClientSource("Neighbourhood", "SimitoneGlobalLink.cs"));
            var stub = CommentsRemoved(SimAntics(Path.Combine("Engine", "TSOGlobalLink",
                "VMTS1GlobalLinkStub.cs")));

            var linkTick = MethodBody(link, "public void Tick(VM vm)");
            var stubTick = MethodBody(stub, "public void Tick(VM vm)");

            //The statements that decide whether a placed object applies at all, and they are the stub's:
            //simulate the command list, and mark it verified when the charge is free or has been paid.
            foreach (var shape in new[]
            {
                "SimulateCommands(cmd.Commands, false)",
                "cmd.Verified = true;"
            })
            {
                Assert.Contains(shape, linkTick);
                Assert.Contains(shape, stubTick);
            }

            //Forwarding goes through one method here - because a forward that throws must not take
            //the drain with it - and that method ends up forwarding exactly what the stub forwards.
            Assert.Contains("ForwardArchitecture(vm, cmd);", linkTick);
            var forward = MethodBody(link,
                "private static void ForwardArchitecture(VM vm, VMNetArchitectureCmd cmd)");
            Assert.Contains("vm.ForwardCommand(cmd);", forward);
            Assert.Contains("vm.ForwardCommand(cmd);", stubTick);

            //the back-pressure is the stub's while-loop condition, and the free path is the stub's
            //"if the cost is zero just send it".
            Assert.Contains("_waitingOnArch && _archBuffer.Count > 0", linkTick);
            Assert.Contains("WaitingOnArch && ArchBuffer.Count > 0", stubTick);
            Assert.Contains("if (cost == 0)", linkTick);
            Assert.Contains("if (cost == 0)", stubTick);

            //and the charge is Math.Abs of the simulated cost with the int.MinValue guard the stub
            //lacks, because Math.Abs(int.MinValue) throws.
            Assert.Contains("cost == int.MinValue ? int.MaxValue : Math.Abs(cost)", linkTick);
            Assert.DoesNotContain("int.MinValue", stubTick);

            //QueueArchitecture is the stub's, plus the one narrowing it is allowed: a null is
            //dropped rather than enqueued, because the queue drains on the game thread.
            var linkQueue = MethodBody(link, "public void QueueArchitecture(VMNetArchitectureCmd cmd)");
            Assert.Contains("if (cmd == null) return;", linkQueue);
            Assert.Contains("_archBuffer.Enqueue(cmd);", linkQueue);
        }

        // ---- helpers ---------------------------------------------------------------------------------

        private static string SimAntics(params string[] file) => CommentsRemoved(
            Read(new[] { "FreeSO", "TSOClient", "tso.simantics" }.Concat(file).ToArray()));

        private static string ClientSource(params string[] parts) =>
            Read(new[] { "Client", "Simitone", "Simitone.Client" }.Concat(parts).ToArray());

        private static string Read(params string[] segments)
        {
            var full = Path.Combine(new[] { Root() }.Concat(segments).ToArray());
            Assert.True(File.Exists(full), "could not find " + full);
            return File.ReadAllText(full);
        }

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; dir != null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "FreeSO", "TSOClient"))) return dir.FullName;
            }
            throw new DirectoryNotFoundException(
                "Could not find FreeSO/TSOClient above " + AppContext.BaseDirectory);
        }

        private static string CommentsRemoved(string source)
        {
            var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
        }

        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "no member starting with: " + signature);
            var open = source.IndexOf('{', at);
            var depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(open, i - open + 1);
                }
            }
            throw new InvalidOperationException("unterminated body for: " + signature);
        }
    }
}