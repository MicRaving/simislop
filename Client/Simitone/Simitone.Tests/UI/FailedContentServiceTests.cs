using System.Collections.Generic;
using FSO.Content.TS1;
using Simitone.Client.UI.Services;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the custom-content warning: which failures are worth telling the player about,
    /// and what the dialog ends up saying.
    ///
    /// <para>
    /// The engine skips a content file it cannot read and records why
    /// (<c>Content.FailedContentFiles</c>). That is the right behaviour and a silent one, so this
    /// is the only place a player finds out that the reason their custom sim is bald is a
    /// truncated <c>.bcf</c> of their own. The parts that decide it - which entries count, which
    /// are repeats of a file already named, and how the error kind is put into words - are pure
    /// functions over that list, and are what these tests pin.
    /// </para>
    ///
    /// <para>
    /// Showing the dialog is not tested: it needs a live screen, and its only decision left is
    /// "once per session", which is a latch rather than logic.
    /// </para>
    ///
    /// <para>
    /// No game install, no content, no graphics device, no VM: the failure list is built by hand.
    /// </para>
    /// </summary>
    public sealed class FailedContentServiceTests
    {
        private static TS1BCFProvider.FailedFileInfo Failure(string filename, string errorType)
        {
            return new TS1BCFProvider.FailedFileInfo
            {
                Filename = filename,
                ErrorType = errorType,
                ErrorMessage = "detail that is not shown to the player"
            };
        }

        // -------------------------------------------------------------------------- short reason

        [Theory]
        [InlineData("EndOfStream", "truncated or corrupt")]
        [InlineData("InvalidData", "invalid format")]
        [InlineData("IOException", "could not be read")]
        [InlineData("CatalogError", "catalog error")]
        [InlineData("DecodeError", "could not be decoded")]
        [InlineData(null, "unknown error")]
        public void ShortReason_PutsTheEngineErrorKindsIntoWords(string errorType, string expected)
        {
            //the engine's kinds are exception type names; a player who cannot act on
            //"System.ArgumentOutOfRangeException" has learned nothing.
            Assert.Equal(expected, FailedContentService.ShortReason(errorType));
        }

        [Fact]
        public void ShortReason_PassesAnUnknownKindThrough()
        {
            //anything the engine did not classify arrives as its own exception type name, and that
            //is the only clue the player has - so it is shown rather than hidden behind "unknown".
            Assert.Equal("SomeException", FailedContentService.ShortReason("SomeException"));
        }

        // -------------------------------------------------------------------------- what to list

        [Fact]
        public void DistinctFailures_IsEmptyForNothingAtAll()
        {
            Assert.Empty(FailedContentService.DistinctFailures(null));
            Assert.Empty(FailedContentService.DistinctFailures(new List<TS1BCFProvider.FailedFileInfo>()));
        }

        [Fact]
        public void DistinctFailures_NamesAFileOnceEvenWhenItFailedThreeWays()
        {
            //one corrupt file can fail its animation, appearance and skeleton passes in turn. Three
            //lines of the same name in one dialog is noise; the first entry is kept, because that
            //is the failure the engine hit first.
            var failures = new List<TS1BCFProvider.FailedFileInfo>
            {
                Failure("mySim.bcf", "EndOfStream"),
                Failure("mySim.bcf", "InvalidData"),
                Failure("mySim.bcf", "IOException"),
            };

            var distinct = FailedContentService.DistinctFailures(failures);
            Assert.Single(distinct);
            Assert.Equal("EndOfStream", distinct[0].ErrorType);
        }

        [Fact]
        public void DistinctFailures_DropsTheEngineTellingItselfSomething()
        {
            var failures = new List<TS1BCFProvider.FailedFileInfo>
            {
                Failure("notes.bcf", FailedContentService.InformationalErrorType),
                Failure("real.bcf", "EndOfStream"),
            };

            var distinct = FailedContentService.DistinctFailures(failures);
            Assert.Single(distinct);
            Assert.Equal("real.bcf", distinct[0].Filename);
        }

        [Fact]
        public void DistinctFailures_IgnoresEntriesWithNothingInThem()
        {
            //defensive rather than expected: a null entry must not take the whole report down.
            var failures = new List<TS1BCFProvider.FailedFileInfo>
            {
                null,
                Failure("real.bcf", "EndOfStream"),
            };

            Assert.Single(FailedContentService.DistinctFailures(failures));
        }

        [Fact]
        public void DistinctFailures_CollapsesFailuresWithNoNameIntoOneEntry()
        {
            //a failure with no file name cannot be told apart from another one, and a dialog line
            //reading " (truncated)" twice helps nobody - so they are reported once.
            var failures = new List<TS1BCFProvider.FailedFileInfo>
            {
                Failure(null, "EndOfStream"),
                Failure("", "IOException"),
            };

            Assert.Single(FailedContentService.DistinctFailures(failures));
        }

        // -------------------------------------------------------------------------- the message

        [Fact]
        public void BuildDetail_IsNothingWhenThereIsNothingToReport()
        {
            //the caller shows no dialog at all rather than an empty one.
            Assert.Null(FailedContentService.BuildDetail(null));
            Assert.Null(FailedContentService.BuildDetail(new List<TS1BCFProvider.FailedFileInfo>()));
            Assert.Null(FailedContentService.BuildDetail(new List<TS1BCFProvider.FailedFileInfo>
            {
                Failure("notes.bcf", FailedContentService.InformationalErrorType)
            }));
        }

        [Fact]
        public void BuildDetail_NamesTheFileAndWhatWentWrongWithIt()
        {
            var detail = FailedContentService.BuildDetail(new List<TS1BCFProvider.FailedFileInfo>
            {
                Failure("mySim.bcf", "EndOfStream")
            });

            Assert.Contains("mySim.bcf", detail);
            Assert.Contains("truncated or corrupt", detail);
        }

        [Fact]
        public void BuildDetail_StaysShortWhenThereAreManyFiles()
        {
            //a player with a hundred broken files does not get a hundred-line dialog; the count
            //tells them how much they are not being shown.
            var failures = new List<TS1BCFProvider.FailedFileInfo>();
            for (int i = 0; i < 25; i++)
            {
                failures.Add(Failure("broken" + i + ".bcf", "EndOfStream"));
            }

            var detail = FailedContentService.BuildDetail(failures);
            for (int i = 0; i < FailedContentService.MaxListedFiles; i++)
            {
                Assert.Contains("broken" + i + ".bcf", detail);
            }
            Assert.DoesNotContain("broken5.bcf", detail);
            Assert.Contains("and 22 more", detail);
        }

        [Fact]
        public void BuildDetail_DoesNotCountTheFilesItManagedToShow()
        {
            var failures = new List<TS1BCFProvider.FailedFileInfo>();
            for (int i = 0; i < FailedContentService.MaxListedFiles; i++)
            {
                failures.Add(Failure("broken" + i + ".bcf", "EndOfStream"));
            }

            Assert.DoesNotContain("more", FailedContentService.BuildDetail(failures));
        }

        [Fact]
        public void BuildDetail_EndsWithTheCausesWorthChecking()
        {
            //a warning the player cannot act on is just an apology; the three causes are the ones
            //that account for nearly every report.
            var detail = FailedContentService.BuildDetail(new List<TS1BCFProvider.FailedFileInfo>
            {
                Failure("mySim.bcf", "EndOfStream")
            });

            Assert.Contains("truncated", detail);
            Assert.Contains(".cfp", detail);
        }

        [Fact]
        public void BuildDetail_DoesNotShowTheEnginesOwnErrorText()
        {
            //the engine's message is a full exception string, which is noise in a dialog; the
            //short reason is what belongs there.
            var detail = FailedContentService.BuildDetail(new List<TS1BCFProvider.FailedFileInfo>
            {
                Failure("mySim.bcf", "EndOfStream")
            });

            Assert.DoesNotContain("detail that is not shown to the player", detail);
        }
    }
}
