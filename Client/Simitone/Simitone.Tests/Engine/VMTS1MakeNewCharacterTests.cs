/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.Utils;
using FSO.Vitaboy;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The pure, testable half of <see cref="VMTS1MakeNewCharacter"/> - the TS1 primitive that
    /// makes a brand new neighbour (a baby arriving, a lot script creating a sim) - plus the
    /// shared hand-slot writer it has in common with the age transform.
    ///
    /// <para>
    /// The primitive itself needs a VM stack frame, a content system and a neighbourhood, so what
    /// is pinned here is the two decisions it makes that are pure arithmetic and that a
    /// neighbourhood playtest would not explain: the six hand strings a new sim is born with, and
    /// the five personality points they start life on.
    /// </para>
    ///
    /// <para>
    /// The hand slots matter because they are read, not interpreted: <c>Outfit.Read</c> takes
    /// slots 17-22 as the left and right idle, pointing and closed hand, and splits each entry at
    /// the first ',' for the mesh and the first '=' for the texture. The tests therefore read the
    /// strings back through <see cref="Outfit"/> and compare them with
    /// <see cref="Outfit.PrepareDefaultHandgroup"/>, which is the engine's own answer to "what
    /// does a hand slot say", rather than against a second guess at the format.
    /// </para>
    /// </summary>
    public sealed class VMTS1MakeNewCharacterTests
    {
        // ---- the six hand strings -----------------------------------------------------------

        [Fact]
        public void AnAdultIsBornWithTheirOwnHands()
        {
            foreach (var gender in new[] { 'm', 'f' })
            {
                var hands = VMTS1BodyTransformHelper.HandBodyStrings(gender, "lgt");

                Assert.Equal("H" + gender + "LO,HAND=huaolgt", hands[VMTS1BodyTransformHelper.SlotHandLeftOpen]);
                Assert.Equal("H" + gender + "RO,HAND=huaolgt", hands[VMTS1BodyTransformHelper.SlotHandRightOpen]);
                Assert.Equal("H" + gender + "LP,HAND=huaolgt", hands[VMTS1BodyTransformHelper.SlotHandLeftPoint]);
                Assert.Equal("H" + gender + "RP,HAND=huaolgt", hands[VMTS1BodyTransformHelper.SlotHandRightPoint]);
            }
        }

        [Fact]
        public void AChildIsBornWithTheGenderlessHands()
        {
            // A child has no gender-specific hand mesh, and MakeNewCharacter has always used "u"
            // for them - the same letter SimTemplateCreateInfo uses for a child's other outfits.
            var hands = VMTS1BodyTransformHelper.HandBodyStrings('u', "drk");
            Assert.Equal("HuLO,HAND=huaodrk", hands[VMTS1BodyTransformHelper.SlotHandLeftOpen]);
            Assert.Equal("HuRC,HAND=huaodrk", hands[VMTS1BodyTransformHelper.SlotHandRightFist]);
        }

        [Fact]
        public void AClosedHandIsNamedClosed()
        {
            // Slot 21 is the left *closed* hand and 22 the right one - Outfit.Read reads them as
            // Fist, and the mesh names tie the hand letter to the gesture ("L" + "C"). This
            // primitive wrote "LO" into slot 21, so a sim born through it was drawn with their
            // open left hand whenever they clenched a fist, while slot 22 in the same six lines
            // said "RC" and the transform's copy of them said "LC".
            var hands = VMTS1BodyTransformHelper.HandBodyStrings('m', "lgt");

            Assert.Equal("HmLC,HAND=huaolgt", hands[VMTS1BodyTransformHelper.SlotHandLeftFist]);
            Assert.Equal("HmRC,HAND=huaolgt", hands[VMTS1BodyTransformHelper.SlotHandRightFist]);
            Assert.NotEqual(hands[VMTS1BodyTransformHelper.SlotHandLeftOpen],
                hands[VMTS1BodyTransformHelper.SlotHandLeftFist]);
        }

        [Fact]
        public void TheHandSlotsNameTheHandsTheEngineAsksFor()
        {
            // The whole six, read back the way the renderer reads them and compared with the
            // engine's own default hand set for the same hand letter.
            var bodyStrings = new STR();
            var handSlots = VMTS1BodyTransformHelper.HandBodyStrings('m', "drk");
            var contents = new List<string>();
            for (int slot = 0; slot < 35; slot++)
            {
                if (slot == 0) contents.Add("adult");
                else if (slot == 1) contents.Add("b007mafit_01,BODY=b007mafitdrk");
                else if (slot == 2) contents.Add("c007ma_01,HEAD-HEAD=c007madrk");
                else if (handSlots.ContainsKey(slot)) contents.Add(handSlots[slot]);
                else contents.Add("");
            }
            for (int i = 0; i < contents.Count; i++)
            {
                bodyStrings.InsertString(0, new STRItem(contents[contents.Count - 1 - i]));
            }

            var outfit = new Outfit();
            outfit.Read(bodyStrings);

            var expected = new Outfit();
            expected.PrepareDefaultHandgroup("m");
            var actual = outfit.LiteralHandgroup;
            var wanted = expected.LiteralHandgroup.LightSkin;

            Assert.Equal(wanted.LeftHand.Idle.Name, actual.LightSkin.LeftHand.Idle.Name);
            Assert.Equal(wanted.LeftHand.Pointing.Name, actual.LightSkin.LeftHand.Pointing.Name);
            Assert.Equal(wanted.LeftHand.Fist.Name, actual.LightSkin.LeftHand.Fist.Name);
            Assert.Equal(wanted.RightHand.Idle.Name, actual.LightSkin.RightHand.Idle.Name);
            Assert.Equal(wanted.RightHand.Pointing.Name, actual.LightSkin.RightHand.Pointing.Name);
            Assert.Equal(wanted.RightHand.Fist.Name, actual.LightSkin.RightHand.Fist.Name);

            // The texture the sim is drawn with is the handgroup token on every one of the six.
            // (The open/point/closed texture *prefix* is the one open question in these strings -
            // Outfit.PrepareDefaultHandgroup uses "huap"/"huac" for the pointing and closed pairs -
            // and it is deliberately left as the open-hand family, because a token that is known
            // to exist as "huao_macssx" is not known to also exist as "huac_macssx".)
            foreach (var gesture in new[]
            {
                actual.LightSkin.LeftHand.Idle, actual.LightSkin.LeftHand.Pointing, actual.LightSkin.LeftHand.Fist,
                actual.LightSkin.RightHand.Idle, actual.LightSkin.RightHand.Pointing, actual.LightSkin.RightHand.Fist,
            })
            {
                Assert.Equal("huaodrk", gesture.TexName);
            }
        }

        [Fact]
        public void ANewCharacterAndAGrownUpOneEndUpWithTheSameHands()
        {
            // The two writers of these six strings have to agree: they are the sim's hands, not the
            // suit's, so whatever wrote them last has to write the same thing. A transform writes
            // them through BuildAdultBodyStrings, a new neighbour through HandBodyStrings.
            var body = new VMTS1BodyTransformHelper.TS1BodyCandidate()
            {
                Mesh = "b007mafit_01", Texture = "b007mafitdrk", Build = "fit", Handgroup = "drk"
            };
            var head = new VMTS1BodyTransformHelper.TS1BodyCandidate()
            {
                Mesh = "c007ma_01", Texture = "c007madrk"
            };
            var grownUp = VMTS1BodyTransformHelper.BuildAdultBodyStrings('m', "drk", body, head);
            var born = VMTS1BodyTransformHelper.HandBodyStrings('m', "drk");

            foreach (var slot in VMTS1BodyTransformHelper.HandSlots)
            {
                Assert.Equal(born[slot], grownUp[slot]);
            }
        }

        // ---- the five personality points -----------------------------------------------------

        [Fact]
        public void ANewSimGetsFivePersonalityPointsInsideTheRange()
        {
            // The draw is the lot's own RNG in VMContext.NextRandom(max) shape - a value in
            // 0..max-1 - so the test's stand-in has the same contract, and a bar can never come
            // out above what the personality panel is able to draw.
            var asked = new List<ulong>();
            var points = VMTS1MakeNewCharacter.RandomPersonalityPoints(max =>
            {
                asked.Add(max);
                return max - 1; // the top of the range, every time
            });

            Assert.Equal(VMTS1MakeNewCharacter.PersonalityPointCount, points.Length);
            Assert.Equal(5, points.Length); // the five bars, not the generous one at slot 4
            Assert.All(asked, max => Assert.Equal((ulong)VMTS1MakeNewCharacter.PersonalityPointMax + 1, max));
            Assert.All(points, p => Assert.InRange(p, 0, (short)VMTS1MakeNewCharacter.PersonalityPointMax));
            Assert.All(points, p => Assert.Equal((short)VMTS1MakeNewCharacter.PersonalityPointMax, p));
        }

        [Fact]
        public void TheRangeIsTheOneTheFixedValueCameFrom()
        {
            // The five bars used to be set to this exact number, all five, for every sim. Widening
            // the range is a question about what the stored number means (UISimCASPanel deals
            // 0-10 a bar; UIPersonalitySubpanel divides what it reads by 100 to show it on the
            // same 0-10 bar), which is not this file's to answer - so the range stays put and only
            // the sameness goes.
            Assert.Equal(500, VMTS1MakeNewCharacter.PersonalityPointMax);
        }

        [Fact]
        public void FiveIdenticalBarsAreNotAPersonality()
        {
            // What the primitive used to produce, every time, for every sim: 500 five times over.
            var seed = 12345UL;
            Func<ulong, ulong> roll = max => max == 0 ? 0 : XorShift(ref seed) % max;

            var points = VMTS1MakeNewCharacter.RandomPersonalityPoints(roll);

            Assert.Equal(5, points.Distinct().Count());
            Assert.All(points, p => Assert.InRange(p, 0, (short)VMTS1MakeNewCharacter.PersonalityPointMax));
        }

        [Fact]
        public void ANewSimsPersonalityIsAsReproducibleAsTheRestOfTheirLot()
        {
            // The body and head come from the lot's RNG so a replay of the same lot is the same
            // sim; the personality has to be dealt from the same source for the same reason.
            var first = DealtPoints(99);
            var second = DealtPoints(99);
            var different = DealtPoints(100);

            Assert.Equal(first, second);
            Assert.NotEqual(first, different);
        }

        [Fact]
        public void NoRngMeansNoPointsRatherThanAnException()
        {
            Assert.Equal(new short[VMTS1MakeNewCharacter.PersonalityPointCount],
                VMTS1MakeNewCharacter.RandomPersonalityPoints(null));
        }

        private static short[] DealtPoints(ulong seed)
        {
            var state = seed;
            return VMTS1MakeNewCharacter.RandomPersonalityPoints(max => max == 0 ? 0 : XorShift(ref state) % max);
        }

        private static ulong XorShift(ref ulong state)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            return state * 2685821657736338717UL;
        }
    }
}
