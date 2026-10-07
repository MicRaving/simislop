namespace Simitone.Client.Utils
{
    /// <summary>
    /// Decides whether the lot the player is currently standing on is safe to write back to disk.
    ///
    /// A TS1 neighbourhood stores every lot as a <c>House{NN}.iff</c> in the user's house folder.
    /// For a family's own home lot that file is the canonical home file - it is what the game
    /// loads next time and what "save" is supposed to mean. The other lots in the same folder
    /// are different: the Downtown (21-31), Vacation (40-49), Studio (81-89) and Magic (90-99)
    /// lots are copies of the templates shipped with the game. When the player is travelling in
    /// one of those lots, the lot's global 10 (house number) reports the travel lot rather than
    /// the family's home, and saving would
    /// <list type="bullet">
    /// <item>overwrite the shipped template lot with the family's house, and</item>
    /// <item>stamp the family's SIMI globals into it,</item>
    /// </list>
    /// which is unrecoverable from inside the game - the template is what every other family, and
    /// every future visit to that destination, starts from. This is the long-standing
    /// "does not support saving on vacation" limitation of this mode; the guard below is what
    /// keeps the data loss from happening silently.
    ///
    /// Refusing the write is only half the answer, because a lot that cannot be written is a lot
    /// that cannot be reloaded either. The other half is <see cref="TravelSave"/>: a travel lot
    /// that is refused here is persisted to its own slot under the user's Simitone folder
    /// instead, and resumed from there on the next visit. The two classes are complementary by
    /// construction - <see cref="TravelSave.IsTravelLot"/> is this guard's exact negation - so
    /// every save path has one home-lot branch and one travel-lot branch and no third case.
    /// </summary>
    public static class LotPersistence
    {
        /// <summary>
        /// Returns true only when <paramref name="lotHouseId"/> is the home lot of the active
        /// family, i.e. when writing to it cannot damage a shipped template.
        /// </summary>
        /// <param name="lotHouseId">The lot the player is currently on, as reported by the VM's
        /// global 10 (house number).</param>
        /// <param name="activeFamilyHouseNumber">The home lot of the family being played, from
        /// their FAMI chunk, or <c>null</c> when no family is active.</param>
        /// <returns>
        /// <c>true</c> when the lot may be persisted; <c>false</c> when the lot is a travel
        /// destination, when the family is unknown, or when the family has no home lot.
        /// </returns>
        public static bool IsPersistableLot(int lotHouseId, int? activeFamilyHouseNumber)
        {
            return activeFamilyHouseNumber.HasValue && lotHouseId == activeFamilyHouseNumber.Value;
        }
    }
}
