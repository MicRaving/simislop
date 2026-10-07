using FSO.SimAntics;
using FSO.SimAntics.Primitives;
using System.Runtime.CompilerServices;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMTS1CallSubject.AsPerson"/> - the rule that an object id which came out of a
    /// register is only usable as a call's subject if it names a person.
    ///
    /// <para>
    /// TS1 generic calls take their subject from <c>Temp0</c> and cast it. <c>Temp0</c> is not a
    /// typed argument - it is whatever the last call on the thread left there - so a script can
    /// put a game object's id in it, and casting a <see cref="VMGameObject"/> to
    /// <see cref="VMAvatar"/> is an <see cref="System.InvalidCastException"/>, <b>not</b> a null.
    /// That is a primitive throwing out of a sim's thread, which the VM turns into a
    /// semantics-exception dialog and a thread reset.
    /// </para>
    ///
    /// <para>
    /// Two call sites had it: call 19 (the Downtown date spawn, shared with call 20, the "take back
    /// home data" variant) null-checked but cast first, and call 21 (the inventory sim-data
    /// effects apply) neither cast-checked nor null-checked before reading
    /// <c>GetPersonData</c>. Call 42, <c>MakeTemp0SelectedSim</c>, already had the check - which
    /// is the proof the author knew and these two were missed.
    /// </para>
    ///
    /// <para>
    /// The cast is tested rather than the call: a <c>VMAvatar</c> and a <c>VMGameObject</c> differ
    /// only in their runtime type here, and that is the whole of what distinguishes the two
    /// answers. The instances are made without running their constructors, which want a marshal, a
    /// lot and a content manager - none of which the type test looks at.
    /// </para>
    /// </summary>
    public sealed class VMTS1CallSubjectTests
    {
        private static T InstanceOf<T>() where T : class
            => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

        [Fact]
        public void AGameObjectIsNotAPerson()
        {
            // The crash. Before the fix this line was `(VMAvatar)context.VM.GetObjectById(temp0)`,
            // which throws for exactly this input - and Temp0 is a register a script can hold a
            // game object id in.
            Assert.Null(VMTS1CallSubject.AsPerson(InstanceOf<VMGameObject>()));
        }

        [Fact]
        public void NothingIsNotAPerson()
        {
            // A dead object id resolves to null, which is the case call 19 already handled and
            // call 21 did not.
            Assert.Null(VMTS1CallSubject.AsPerson(null));
        }

        [Fact]
        public void APersonIsTheSamePerson()
        {
            var ava = InstanceOf<VMAvatar>();
            Assert.Same(ava, VMTS1CallSubject.AsPerson(ava));
        }

        [Fact]
        public void TheRefusalIsNullAndNotAnException()
        {
            // The difference between `as` and a hard cast, stated so the primitive's contract is
            // "refuse" rather than "throw": the caller turns null into GOTO_FALSE, which every
            // retail call site treats as "this was not applicable", not into a thread reset.
            var gameObject = InstanceOf<VMGameObject>();

            Assert.Null(VMTS1CallSubject.AsPerson(gameObject));
            Assert.Null(VMTS1CallSubject.AsPerson(null));
        }

        [Fact]
        public void TheTwoSubjectsCall19And21CanReceiveAreOnOppositeSidesOfTheTest()
        {
            // What makes the check necessary at all: the entity a game-object id resolves to and
            // the entity a person id resolves to are both VMEntity, and only one of them is a
            // VMAvatar. A register holds an object id, so it can hold either.
            Assert.True(typeof(VMEntity).IsAssignableFrom(typeof(VMAvatar)));
            Assert.True(typeof(VMEntity).IsAssignableFrom(typeof(VMGameObject)));
            Assert.False(typeof(VMAvatar).IsAssignableFrom(typeof(VMGameObject)));
        }
    }
}
