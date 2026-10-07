# Audit — content loading, file formats, save/persistence

**Scope:** `FreeSO/TSOClient/tso.content/**` (providers, TS1 loaders, caches), `FreeSO/TSOClient/tso.files/**` (IFF/FAR/chunk parsers, RC formats), the Simitone save/travel/neighbourhood stores, and the *effectiveness* of `Client/Simitone/Simitone.Tests/**`.

**Method:** read-only source review. No source was modified. The test project was compiled headlessly (`dotnet build -t:Compile`, 0 errors / 211 warnings) to confirm line numbers and to check what the suite can and cannot reach.

**How to read this:** findings are ordered by severity. **§1 is the highest-risk data-loss surface** — every path that can destroy or silently corrupt a player's own save. **§2** is parser correctness (crashes and silent corruption from malformed input). **§3** is cache/lifetime and load-order. **§4** is bad practices. **§5** is test effectiveness. **§6** is the fix order.

---

## 1. Highest-risk data-loss paths

### F-01 · `SIMI` budget day: the wrong field is written into the bills slot, on every save
**`FreeSO/TSOClient/tso.files/Formats/IFF/Chunks/SIMI.cs:162`** · severity **Critical** · confidence **High**

```csharp
155: io.WriteInt32(Valid);
156: if (Valid == 0) return;
157: io.WriteInt32(MiscIncome);
158: io.WriteInt32(JobIncome);
159: io.WriteInt32(ServiceExpense);
160: io.WriteInt32(FoodExpense);
161: io.WriteInt32(BillsExpense);
162: io.WriteInt32(MiscIncome);      // <-- should be MiscExpense
163: io.WriteInt32(HouseholdExpense);
164: io.WriteInt32(ArchitectureExpense);
```

`SIMIBudgetDay.Read` (line 139-151) reads, in order: `Valid, MiscIncome, JobIncome, ServiceExpense, FoodExpense, BillsExpense, MiscExpense, HouseholdExpense, ArchitectureExpense`. `Write` emits `…, BillsExpense, MiscIncome, …` — so wire slot 5 is `BillsExpense` on write and `MiscIncome`. Every read-back therefore yields `BillsExpense = <last session's MiscIncome>` and the real `MiscExpense` is never written at all. The seven fields are shifted, not just one: `MiscExpense` is silently discarded.

**Why it matters:** this is reached from `SaveService.Save` → `BuildHouseFileBytes` → `iff.Write` on the player's own `House{NN}.iff`, every time they save a lot, and it is a *silent* value corruption — no exception, no log, and the on-disk file is structurally valid so the next load succeeds and shows wrong numbers. This is the worst class of bug in the codebase: undetectable, cumulative, and inside the file the player is told is safely committed.

**Not caught by tests.** Every `SIMIBudgetDay` in the test suite is default-constructed (`SaveCompatTests.cs:148`, `SaveServiceTests.cs:103`, `NeighborhoodServiceTests.cs:428`), so `Valid == 0` and `Write` returns at line 156. The buggy branch is never executed by any test in the repository (see **T-05**).

**Fix:** `io.WriteInt32(MiscExpense);` at line 162. Add a round-trip test that sets all nine fields to distinct non-zero values with `Valid = 1` and asserts every one comes back (this is the test that should have existed and is the cheapest possible guard against the whole class).

---

### F-02 · `SIMI.Write` stamps a hardcoded version but derives the payload length from a different value; `Read` uses a *third* threshold
**`FreeSO/TSOClient/tso.files/Formats/IFF/Chunks/SIMI.cs:85,87` (and `:38`)** · severity **Critical** · confidence **High**

```csharp
// Read  :38   var items = (Version > 0x3F) ? 0x40 : 0x20;   // Version from the file
// Write :85   io.WriteInt32(0x3E);                          // version hardcoded
// Write :87   var items = (Version > 0x3E) ? 0x40 : 0x20;   // Version from the *instance*
```

Three different sources of truth for one field. Consequences:

1. **A chunk with `Version` in `(0x3E, 0x3F]`** — writes 64 shorts but stamps `0x3E`. On reload, `Read` sees `0x3E` → reads 32 globals → `Unknown1`, `Unknown2`, `Unknown3`, `GUID1`, `GUID2`, `Unknown4`, **`LotValue`, `ObjectsValue`, `ArchitectureValue`** are all read out of the *globals* that followed. `PurchaseValue` is then garbage.
2. **Any `Version > 0x3E`** that reaches `Write` without going through `VMTS1LotState.UpdateSIMI` (which pins `Version = 0x3E`, `VMTS1LotState.cs:150`) hits the same path. `IffFile.Write` is reachable from `ChangeManager.SaveChange`, from `NeighborhoodService.BuildResetHouseFileBytes` (eviction), and from any read→re-serialise path.
3. **Read and write disagree on the boundary**: `> 0x3F` vs `> 0x3E`. Even a self-consistent file cannot round-trip at `Version == 0x3F`.

`SIMI.Write` is also the only chunk writer in the file that guards its array (`GlobalData = GlobalData ?? new short[38]`, line 92) but then indexes `BudgetDays[i]` for `i` 0..5 **unguarded** (line 114). A chunk with `BudgetDays == null` or shorter than six throws `NullReferenceException` / `IndexOutOfRangeException` out of the middle of the save. Both test helpers work around this by hand-building a six-element array (`SaveServiceTests.cs:97-104`, `SaveCompatTests.cs:143-149`) rather than fixing the writer — the test suite is shaped around the defect.

**Fix:**
```csharp
const uint Version = 0x3E;
io.WriteInt32((int)Version);
var items = Version > 0x3F ? 0x40 : 0x20;   // same predicate as Read
…
BudgetDays ??= new SIMIBudgetDay[6];
for (int i = 0; i < 6; i++) (BudgetDays[i] ??= new SIMIBudgetDay()).Write(io);
```
Add a `[Theory]` over `Version` ∈ {0x3E, 0x3F, 0x40} asserting the written file re-reads with the same `LotValue`/`ObjectsValue`/`ArchitectureValue` and the same first 32 globals.

---

### F-03 · `Neighborhood.iff` — the most valuable file in a save — is truncated in place, with no temp, no backup, no atomic swap
**`FreeSO/TSOClient/tso.content/TS1/TS1NeighbourProvider.cs:532-533`** · severity **Critical** · confidence **High**

```csharp
532: using (var stream = new FileStream(Path.Combine(UserPath, "Neighborhood.iff"),
533:          FileMode.Create, FileAccess.Write, FileShare.None))
534:     MainResource.Write(stream);
```

`FileMode.Create` truncates at open. `MainResource.Write` then serialises every FAMI (families, budgets, `HouseNumber`), every NBRS (neighbours), the NGBH (magicoins inventory) and the TATT. Any failure part way through — disk full, an exception in a chunk's `Write`, AV interference, a power cut, a crash — leaves a **truncated neighbourhood file with no recovery copy**. It is unrecoverable from inside the game.

The inconsistency is the tell: the far less valuable `House{NN}.iff` goes through `AtomicFileCommit` (`SaveService.cs:158`), the travel slot does too (`SaveService.cs:295`), and even the regenerable JSON sidecar does (`NeighbourhoodLayoutStore.cs:202`). `Neighborhood.iff` holds *budgets, families and every lot purchase* and is the only one of the four written destructively. `SaveCompatTests.cs:33-34` even claims "the commit is a single atomic swap" while testing only the house file.

A second, quieter problem: `IffFile.WriteChunk` mutates `c.OriginalData = data` (line 273) *before* writing. A failed write therefore leaves the in-memory `MainResource` holding half-regenerated chunk data, so an immediate retry is not a clean retry.

**Fix:** serialise fully into a `MemoryStream` first, then
```csharp
AtomicFileCommit.Write(Path.Combine(UserPath, "Neighborhood.iff"), bytes);
```
That gives the same stage/backup/commit contract as every other save, at the cost of one buffer. If the file is large enough that matters, use `File.Replace(tmp, path, bak)` (a single atomic step) instead of copy-then-delete. Add a test that a `SaveNeighbourhood` over an existing neighbourhood leaves a readable `.bak`.

---

### F-04 · `AtomicFileCommit` is not atomic: the commit step is a copy, not a rename
**`Client/Simitone/Simitone.Client/Utils/AtomicFileCommit.cs:84-85`** · severity **High** · confidence **High**

```csharp
84: File.Copy(tmpPath, path, true);
85: File.Delete(tmpPath);
```

The class doc (lines 21-23) says "File.Move cannot overwrite portably, hence copy-then-delete. A failure part way through this step still leaves a complete .tmp and a complete .bak, so the data is never lost." The second clause is true; the first is now false. .NET Core 3.0+ (this is `net9.0`) has `File.Move(src, dst, overwrite: true)`, which maps to `MoveFileEx` with `MOVEFILE_REPLACE_EXISTING` — atomic on the same volume. But the real primitive is `File.Replace(tmpPath, path, bakPath)`: one syscall, atomic, *and* it writes the backup as part of the same operation.

As written, `File.Copy(tmp, path, true)` opens the target with `CREATE_ALWAYS` — the live `House{NN}.iff` is truncated and refilled byte-by-byte. A mid-copy failure leaves a **partially written house file** plus a complete `.tmp` and `.bak`. The player's house is then un-loadable on next launch; recovery requires manually renaming the `.bak`, which nothing in the game ever offers. This is precisely the failure the class exists to prevent, and the suite cannot detect it (**T-12**).

Two smaller defects in the same method: `stream.Flush()` (line 71) flushes to the OS page cache, not to disk — the staged `.tmp` is not durable across a power cut unless `FileOptions.WriteThrough` is used or `Flush(true)` is called. And `path` is never validated as rooted, so a relative `path` silently resolves against the process working directory, which `Program.cs:31` deliberately sets to the install folder.

**Fix:**
```csharp
if (File.Exists(path)) File.Replace(tmpPath, path, bakPath);   // atomic; also writes the .bak
else                 File.Move(tmpPath, path);
return File.Exists(bakPath) ? bakPath : null;
```
Open the staged stream with `FileOptions.WriteThrough` and `stream.Flush(true)`. Assert `Path.IsPathRooted(path)` or throw, so a relative save path can never reach the CWD.

---

### F-05 · `LoadCharacters` aborts the whole character load on the first non-`.iff` file
**`FreeSO/TSOClient/tso.content/TS1/TS1NeighbourProvider.cs:362`** · severity **High** · confidence **High**

```csharp
359: var files = Directory.EnumerateFiles(path);
360: foreach (var filename in files)
361: {
362:     if (Path.GetExtension(filename) != ".iff") return;   // should be `continue`
```

`return` leaves the method, not the iteration. One file that is not lowercase `.iff` — and there are several reasons for one to exist, including the `.bak` side files `AtomicFileCommit` itself creates for any `House{NN}.iff` in a sibling folder, plus `Thumbs.db`, a `.tmp` from an interrupted commit, or simply a `.IFF` on a case-preserving filesystem — makes **every character after it in enumeration order silently disappear** for the rest of the session. The extension test is also case-sensitive, so a legitimately named `.IFF` is treated as foreign.

The same `Directory.EnumerateFiles` has no error handling: a missing `Characters` folder throws `DirectoryNotFoundException` out of `TS1NeighborhoodProvider.InitSpecific`, i.e. out of content init with the window already up.

The production behaviour is *documented as a known wart by a test* — `TS1AvatarSavePathTests.cs:181`: "LoadCharacters also returns out of the whole loop on a non-.iff, so writing one would be doubly pointless". The test pins the surrounding logic around a bug instead of fixing it.

**Fix:** `continue`, `StringComparison.OrdinalIgnoreCase` on the extension, and wrap the enumeration in a try/catch that logs the directory and returns an empty set. Add a test that a folder containing `User00001.iff`, `notes.txt`, `User00002.iff` loads *both* characters.

---

### F-06 · First-run neighbourhood bootstrap: unguarded recursive copy, and "folder exists" is mistaken for "folder complete"
**`FreeSO/TSOClient/tso.content/TS1/TS1NeighbourProvider.cs:109-115, 296-312`** · severity **High** · confidence **High**

```csharp
109: if (!Directory.Exists(userPath))
114:     CopyFolder(ResolveBootstrapSource(id, udName), userPath);
…
303: foreach (var dirPath in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
310:     File.Copy(srcPath, CombineRelative(destinationRoot, srcPath, sourceRoot), true);
```

Neither `GetDirectories` nor `GetFiles` is guarded. One unreadable subfolder (an ACL, a OneDrive placeholder, a broken reparse point) throws `UnauthorizedAccessException` **mid-copy**, leaving a half-populated `UserData` in the player's Documents folder. On the next launch, `Directory.Exists(userPath)` is `true`, the copy is skipped, and the game proceeds to `new IffFile(Path.Combine(UserPath, "Neighborhood.iff"))` (line 119) against an incomplete tree. The result is a crash whose message names a file the player has never seen.

`NeighbourhoodManager.CopyDirectory` (`Client/.../Utils/NeighbourhoodManager.cs:1154-1185`) is the **hardened twin of this exact function** — per-file and per-directory try/catch, a failure count, and `DiscardPartialCopy` (line 1127-1140) on any failure. Two implementations of the same bootstrap exist in the codebase; only the manual one is safe, and the automatic first-run path uses the unsafe one.

`CombineRelative` (line 314-318) also does `fullPath.Substring(sourceRoot.Length)` with no length check, which throws if the enumeration's separator style ever differs from `sourceRoot`'s.

**Fix:** delete `CopyFolder` and call the existing `NeighbourhoodManager` copy path; or at minimum wrap both loops, and change the guard on line 109 from `Directory.Exists(userPath)` to "exists **and** contains `Neighborhood.iff`", with a partial copy deleted on failure.

---

### F-07 · `SaveAvatarIff` / `SaveNewNeighbour` overwrite existing user files non-atomically and report failures to `Console`
**`FreeSO/TSOClient/tso.content/TS1/TS1NeighbourProvider.cs:577, 400, 570, 582, 586`** · severity **High** · confidence **High**

Both avatar writes use `FileMode.Create` on a path that already holds a player's character. A failure part way through leaves a truncated `Characters/User000NN.iff` — the family's Sims gone. The decision logic (`TS1AvatarSavePath.Resolve`) is excellent and carefully tested; the *write* was never brought under the same contract.

The two failure branches report to `Console.WriteLine` (lines 570, 582, 586) rather than `SimitoneLog`. In a shipped Windows game with no attached console that is **no log at all** — a save failure is completely silent. The class already depends on `Simitone.Client.Utils`-adjacent logging elsewhere in the client; the provider does not.

Note also that `catch (IOException)` / `catch (UnauthorizedAccessException)` do not cover an exception thrown from inside `resource.MainIff.Write(stream)` (e.g. a chunk's `Write` throwing, or `IffFile.WriteChunk` NREing on a chunk type absent from `CHUNK_TYPES`) — that escapes `SaveAvatarIff`, escapes `SaveNeighbourhood`, and takes the whole neighbourhood save with it. The class doc on `TS1AvatarSavePath.cs:117-122` describes exactly this ("the avatar loop ran before the Neighborhood.iff write and did not catch anything") as fixed; it is only fixed for IO exceptions.

**Fix:** `AtomicFileCommit.Write(target.Path, serialisedBytes)` for both, `SimitoneLog.Warn` instead of `Console.WriteLine`, and a final `catch (Exception ex) { SimitoneLog.Error(...); }` so no avatar failure can abort the neighbourhood save.

---

### F-08 · `LocalHouse/*.fsov` blueprint-reset: unguarded `catch (Exception)` and the exact silent-`VMMarshal` trap the travel path already guards
**`Client/Simitone/Simitone.Client/UI/Services/LotLifecycleService.cs:225-261`** · severity **High** · confidence **High**

```csharp
225: try   { … File.OpenRead(… "LocalHouse/" + filename.Substring(0, filename.Length - 4) + ".fsov") … marshal.Deserialize(file); vm.Load(marshal); }
240: catch (Exception)      // no log, no distinction
242: { … File.ReadAllBytes(path) … VMBlueprintRestoreCmd … }
```

Two defects, both named elsewhere in this codebase and both left unfixed here:

1. **No log.** A missing cache file, a corrupt one, and a genuine bug inside `vm.Load` are indistinguishable afterwards. The reset silently succeeds and the player's cached rebuild is discarded.
2. **No `FSOv` preamble check.** `VMMarshal.Deserialize` (`FreeSO/.../Marshals/VMMarshal.cs:46`) is `if (new string(reader.ReadChars(4)) != "FSOv") return;` — it returns a **hollow marshal** with null `Context` and null `Entities` rather than throwing. `TravelSave.HasMarshalPreamble` and `SaveService.TryRestoreTravelSlot` (line 376) exist specifically to check for this before deserialising; the blueprint path does not, so a truncated `.fsov` is loaded into a live VM.

Also: `filename.Substring(0, filename.Length - 4)` throws for a name shorter than four characters, and `JobLevelFromPath` (line 133) uses culture-sensitive `short.Parse` and throws `FormatException`/`ArgumentOutOfRangeException` **from inside the catch block**, so it escapes `BlueprintReset` entirely.

**Fix:** read the file into a byte array, `TravelSave.HasMarshalPreamble(bytes, 0)` before deserialising, `catch (Exception ex) { SimitoneLog.Warn("…", ex); … }`, and give `JobLevelFromPath` a `short.TryParse` with `NumberStyles.None, CultureInfo.InvariantCulture` returning `-1`.

---

### F-09 · `VMMarshal.Deserialize` — every array length comes straight off the wire, unchecked
**`FreeSO/TSOClient/tso.simantics/Marshals/VMMarshal.cs:53-54, 68-69, 78-79, 86-87, 94-95`** · severity **High** · confidence **High**

```csharp
53: var length = reader.ReadInt32();
54: var cStream = new MemoryStream(reader.ReadBytes(length));   // negative -> throws; huge -> OOM
…
68: int ents = reader.ReadInt32();
69: Entities = new VMEntityMarshal[ents];                     // negative -> OverflowException; huge -> OOM
78: int thrN = reader.ReadInt32();   86: int mtgN = reader.ReadInt32();
94: int globs = reader.ReadInt32(); 95: GlobalState = new short[globs];
```

Not one of these is validated against the remaining stream length, against a sane maximum, or against `LATEST_VERSION`. This is the parser for the blob inside the player's `House{NN}.iff` `FSOV` chunk and the travel slot — i.e. exactly the files the audit's data-integrity brief is about. A truncated or bit-rotted save produces `OutOfMemoryException` (a value near `int.MaxValue` makes `BinaryReader.ReadBytes` attempt a 2 GB allocation) or an unhandled `ArgumentOutOfRangeException` from deep inside `VM.Load`, where the player sees a crash rather than "this save is damaged, here is your .bak".

**Fix:** a private `static int ReadCount(BinaryReader r, long remaining, int max)` that rejects negatives, anything above `max`, and anything above the bytes actually left; throw a typed `MarshalFormatException` carrying the field name. One test per field with a negative, a `int.MaxValue`, and a plausible-but-too-large value.

---

### F-10 · `IffFile.Read` — unsigned chunk-size arithmetic wraps, and the skip can seek backwards
**`FreeSO/TSOClient/tso.files/Formats/IFF/IffFile.cs:193-206`** · severity **High** · confidence **High**

```csharp
193: var chunkType = io.ReadCString(4);
194: var chunkSize = io.ReadUInt32();
…
198: var chunkDataSize = chunkSize - 76;                                  // uint arithmetic
201: if (!CHUNK_TYPES.ContainsKey(chunkType)) {
204:     io.Skip(Math.Min(chunkDataSize, stream.Length - stream.Position - 1));
205:     return null; }
216: newChunk.ChunkData = io.ReadBytes(chunkDataSize);
```

`chunkSize` is `uint` and `76` is an `int` literal, so `chunkDataSize` is a `uint`. Any `chunkSize < 76` — which is what a truncated tail or a corrupt size field looks like — wraps to ~4.29 billion, and `IoBuffer.ReadBytes(uint)` does `Reader.ReadBytes((int)num)` with a **negative** count → `ArgumentOutOfRangeException` out of `Read`, uncaught, for a file the player merely opened.

At line 204, when the position is at EOF, `stream.Length - stream.Position - 1` is `-1`, so `Math.Min` returns `-1` and `IoBuffer.Skip` (`IoBuffer.cs:56-59`) seeks **backwards**. The `while (io.HasBytes(2))` loop at line 167 then re-reads the same bytes: a potential infinite loop / hang inside content load.

**Fix:** `if (chunkSize < 76) throw new IffFormatException(...)`; compute the payload as `int` after that check; clamp the skip to `Math.Max(0, stream.Length - stream.Position - 1)`; wrap the whole `Read` body in a try/catch that rethrows as a typed format exception naming the file. Add the four missing tests: `chunkSize == 0`, `chunkSize == 75`, `chunkSize` past EOF, and a valid file with an unknown chunk type at the end.

---

## 2. Parser correctness

### F-11 · `FAR3Archive`'s decompressed size is read **big-endian** while the writer emits little-endian
**`FreeSO/TSOClient/tso.files/FAR3/FAR3Archive.cs:113`** · severity **High** · confidence **High**

```csharp
 65: Entry.CompressedFileSize  = (uint)((dummy0 << 0) | (dummy1 << 8) | dummy2 << 16);   // little-endian
113: uint DecompressedSize     = (uint)((dummy0 << 0x10) | (dummy1 << 0x08) | +dummy2);   // big-endian
```

Three independent signals that line 113 is the wrong one: the sibling field at line 65 reads the same three bytes little-endian; `Decompresser.Compress` writes the same field little-endian (`Decompresser.cs:276`, `revData[0] | revData[1] << 8 | revData[2] << 16`); and the stray unary `+dummy2` is a leftover from a bad edit. A size of `0x00ABCD` is read as `0x00CDAB` — 8192× too large. This value is the destination buffer length in `Decompresser.Decompress` (line 304), so the consequence is a huge allocation or an `OverflowException` on every compressed FAR3 entry, not a small numerical error.

**Fix:** `uint DecompressedSize = (uint)(dummy0 | (dummy1 << 8) | (dummy2 << 16));`. Add a `Compress` → `Decompress` round-trip test with a >64 KB input (so all three size bytes are non-zero) and assert byte equality.

---

### F-12 · `FAR3Archive`'s process-wide static re-entrancy flag silently yields empty archives and is leaked on every throw
**`FreeSO/TSOClient/tso.files/FAR3/FAR3Archive.cs:14, 30-32, 40, 48, 85`** · severity **High** · confidence **High**

```csharp
14: public static bool isReadingSomething = false;
30: if (isReadingSomething == false) {
32:     isReadingSomething = true;
       …
40:     throw new FAR3Exception("Could not open …");
48:     throw new FAR3Exception("Archive wasn't a valid FAR V.3 archive!");
85:     isReadingSomething = false; }
```

(a) **Concurrency:** if any thread is inside the constructor, another thread's `new FAR3Archive(...)` takes the `if` branch, does *nothing*, and returns a valid-looking object with `m_Reader == null` and three empty dictionaries. No exception, no log. `Content.GetResource` (line 404) then caches that empty archive in `Content.Archives` permanently. First use of it does `lock (m_Reader)` on `null` → `ArgumentNullException`.

(b) **The flag leaks on every throwing path.** It is set at line 32 and only cleared at line 85. If the header check at line 48 throws — i.e. **one** malformed or truncated `.dat`/`.far3` anywhere in the install — `isReadingSomething` stays `true` for the rest of the process and **every subsequent archive construction is a silent no-op**. One bad file disables all avatar/`uigraphics` content for the session, with nothing in the log.

`GetEntry` (lines 100, 122, 133, 144) sets the same flag and clears it on four of five paths; a throw from `Decompress` or `ReadBytes` leaves it set, with the same poisoning effect.

**Fix:** make the guard `lock (m_Reader)` on a per-instance reader and drop the static entirely; if a process-wide guard is genuinely needed, make it a `try/finally` around the whole constructor body.

---

### F-13 · `TimedReferenceCache` — `ObjectToRing.Remove(oldring)` should be `Remove(o)`: unbounded strong-reference leak
**`FreeSO/TSOClient/tso.common/Utils/TimedReferenceCache.cs:102-113`** · severity **High** · confidence **High**

```csharp
102: if (ObjectToRing.TryGetValue(o, out oldring)) {
104:     if (becomes != oldring) {
106:         ReferenceRing[oldring].Remove(o);
107:         ObjectToRing.Remove(oldring);      // <-- key is an int; the entry for `o` is never removed
     } }
```

`ObjectToRing` is `Dictionary<object, int>` — a **strong** reference to every object ever kept alive. The removal is keyed on the ring index rather than the object, so it never removes anything (and could, in principle, remove an unrelated entry if a key equal to that small int existed). An object that is dereferenced and re-referenced therefore stays in `ObjectToRing` forever: the timed cache's entire release mechanism is defeated and the "cache" becomes a leak proportional to every distinct content object ever loaded. This is the cache behind every FAR/FAR3/IFF lookup in the game.

Two adjacent defects in the same class:
- Line 166, `else if (refVal != null || value.IsAlive) return (VALUE)refVal;` — the `value.IsAlive` arm permits `refVal == null`, so `GetOrAdd` can return a null/default `VALUE` to a caller that believes it got an object. For a reference `T` that is a null texture/mesh handed to the renderer; for a value `T` it is a `NullReferenceException` on unbox.
- Line 161, `PermaRef.Add(created)` on a plain `List<VALUE>` outside any lock, while `GetOrAdd` is documented as thread-safe.

**Fix:** `ObjectToRing.Remove(o);`; tighten line 166 to `if (refVal != null) return (VALUE)refVal;`; make `PermaRef` a `ConcurrentBag<VALUE>` or take `lock (this)`.

---

### F-14 · `FAR1Provider` caches by entry filename alone, so two archives with the same name collide silently
**`FreeSO/TSOClient/tso.content/Framework/FAR1Provider.cs:130`** · severity **High** · confidence **High**

```csharp
130: return Cache.GetOrAdd(entry.FarEntry.Filename, (name) => { … entry.Archive.GetEntry(entry.FarEntry) … });
```

The key is the bare filename; the value comes from `entry.Archive`. TS1 `.far` files routinely contain same-named entries (a `walking.iff` in `Sims_1.1.far` and again in `Sims_2.far`, an `Objects.iff` in several). Whichever archive is opened first wins the key **forever**, and every later request for the other archive's entry returns the first archive's bytes. The result is a wrong mesh, wrong texture, or a wrong `Global.iff` with no error anywhere. `FAR3Provider` gets this right — it keys on `Entry.ID` (line 140), which is unique.

**Fix:** key on a composite of archive path and entry filename, e.g. `entry.Archive.GetHashCode() + ":" + entry.FarEntry.Filename`, or store the archive path on the `Far1ProviderEntry` and key on that.

---

### F-15 · `Decompresser.Decompress` returns a zero-padded buffer instead of failing
**`FreeSO/TSOClient/tso.files/FAR3/Decompresser.cs:304, 310, 412`** · severity **High** · confidence **High**

```csharp
304: byte[] DecompressedData = new byte[(int)m_DecompressedSize];
310: while (Control1 != 0xFC && Pos < Data.Length) { … }
412: return DecompressedData;
```

`m_DecompressedSize` is unvalidated and, per **F-11**, currently miscomputed. Every inner `break` on `DataPos == DecompressedData.Length`, and the loop-exit at `Pos >= Data.Length`, return an array whose tail is **all zeros**. A truncated compressed entry therefore yields a plausible-looking buffer with a silently wrong tail rather than an exception. `ArrayCopy2`/`OffsetCopy` (lines 41-76) have no bounds checks and `OffsetCopy`'s `srcPos = destPos - srcPos` can go negative → `IndexOutOfRangeException`; the `(int)` cast at line 45 wraps a large `long`.

**Fix:** bound `m_DecompressedSize` against `Data.Length` before allocating, throw on a mismatch between the produced length and the declared length, and add `if (SrcPos + i < Src.Length)` guards.

---

### F-16 · `DBPFFile` — the stream is never disposed, and `m_EntriesByType` is not reset between reads
**`FreeSO/TSOClient/tso.files/Formats/DBPF/DBPFFile.cs:41-42, 51-52, 122-125, 138, 164`** · severity **Medium-High** · confidence **High**

```csharp
41: var stream = File.OpenRead(file);
42: Read(stream);                        // stream never stored, never disposed
…
51: m_EntryByID  = new Dictionary<ulong, DBPFEntry>();
52: m_EntriesList = new List<DBPFEntry>();   // m_EntriesByType is NOT reset
…
122: if (!m_EntriesByType.ContainsKey(entry.TypeID))
123:     m_EntriesByType.Add(entry.TypeID, new List<DBPFEntry>());
```

`Dispose()` (line 177) calls `Io.Dispose()`, and `IoBuffer.Dispose()` (`IoBuffer.cs:320-322`) is an **empty method** — so the `FileStream` opened at line 41 leaks for the process lifetime, and `DBPFFile : IDisposable` promises a release that does not happen.

A second `Read` on the same instance accumulates into the previous `m_EntriesByType`, so `GetItemsByType` (line 164) can return entries whose `FileOffset` points into the *new* stream — garbage, silently. `GetEntry` (line 138) does `ReadBytes((int)entry.FileSize)` with no validation against the stream length and **no lock**, mutating the shared reader position; `FAR3Archive.GetEntry` does take `lock (m_Reader)`, so the inconsistency is a bug, not a house style. `m_EntriesByType[Type]` (line 164) throws `KeyNotFoundException` for an unknown type rather than returning empty.

**Fix:** store the stream and dispose it in `Dispose()` (or make `IoBuffer.Dispose` actually dispose its `BinaryReader`); reset `m_EntriesByType` at line 52; validate `FileOffset`/`FileSize` against `stream.Length`; use `TryGetValue`; `lock` the reader in `GetEntry`.

---

### F-17 · `PiffEncoder.MakeChunkDiff` dereferences before its own null check, and NREs whenever `OriginalData` is absent
**`FreeSO/TSOClient/tso.files/Formats/PiffEncoder.cs:101-106, 129`** · severity **High** · confidence **High**

```csharp
101: var e = new PIFFEntry { Type = chk.ChunkType, ChunkID = chk.OriginalID, NewChunkID = chk.ChunkID };
102: if (chk == null)                      // unreachable: chk was already dereferenced
103: { e.EntryType = PIFFEntryType.Remove; return e; }
…
123: var oldData = chk.OriginalData;
129: if (i >= oldData.Length)             // NRE when OriginalData is null
```

`OriginalData` is only populated when `IffFile.RETAIN_CHUNK_DATA` is true (`IffFile.cs:24`), which defaults to `false` and is documented as "should really only be set when the user wants to use the IDE". So for any normal user, `MakeChunkDiff` throws `NullReferenceException` for any chunk in `Modified`/`Patched` state — i.e. `ChangeManager.SaveChange` (line 154) throws for every content edit, and no user patch can be written. (The `else` branch of `PIFFEntryType` at line 40 is the only reason anything appears to work: `Patch == 0` is the enum's default, so an unset `EntryType` happens to be correct. Reordering `PIFFEntryType` would silently break every patch.)

There is **no test anywhere** that applies a generated PIFF and checks the result equals the chunk's new data — the diff/apply pair (`MakeChunkDiff` → `PIFFEntry.Apply`) is entirely unverified despite being the format for all user content patches.

**Fix:** move the null check first; `var oldData = chk.OriginalData ?? new byte[0];` (or force `RetainChunkData` for the editing path); set `e.EntryType = PIFFEntryType.Patch` explicitly rather than relying on the default; add a `Generate → Write → Read → Apply == newData` round-trip test over a chunk with a mid-string edit, a truncation, and a growth.

---

### F-18 · `PIFF.Read` / `PIFFEntry.Apply` — unchecked file-supplied counts, and a `uint` underflow
**`FreeSO/TSOClient/tso.files/Formats/IFF/Chunks/PIFF.cs:64-77, 139-164`** · severity **Medium-High** · confidence **High**

```csharp
64: var size = io.ReadUInt32();
65: e.Patches = new PIFFPatch[size];        // 4 billion -> OOM
72: p.Offset = lastOff + io.ReadVarLen();   // uint overflow possible
77: if (p.Mode == PIFFPatchMode.Add) p.Data = io.ReadBytes(p.Size);
…
141: var result = new byte[NewDataSize];     // uint from file; >= 2^31 -> OverflowException
147: var copyCount = p.Offset - destPtr;    // uint: if p.Offset < destPtr this wraps to ~4e9
148: Array.Copy(src, srcPtr, result, destPtr, copyCount);
```

`Apply` validates nothing. It is called from `IffFile.Patch` (line 542) with `chunk.ChunkData ?? chunk.OriginalData`, which can be `null` → `Array.Copy(null, …)`. `IffFile.Patch` line 526 and `Revert` line 498 also do unguarded `CHUNK_TYPES[e.Type]` lookups on a **file-supplied string**, so a third-party `.piff` naming a chunk type this build does not know throws `KeyNotFoundException` out of `SetFilename` → out of content init → the game will not start because of a user-installed patch. `IffFile.Patch` line 515 and `Revert` line 495 do `piffFile.List<PIFF>()[0]` with no null check.

**Fix:** bound every count against the bytes remaining; use `long`/checked arithmetic in `Apply` and throw a typed exception with the piff's `SourceIff`; `TryGetValue` for `CHUNK_TYPES`; guard the `List<PIFF>()[0]`.

---

### F-19 · `IoBuffer` — no bounds checks anywhere; four independent throw/hang paths on malformed input
**`FreeSO/TSOClient/tso.files/Utils/IoBuffer.cs:47-50, 77-89, 228-232, 238-253, 275-288`** · severity **Medium** · confidence **High**

- `ReadLongPascalString` (line 230-231): `ReadInt16()` can be negative; `ReadBytes(negative)` throws `ArgumentOutOfRangeException`.
- `ReadNullTerminatedString` (line 245-251) and `ReadNullTerminatedUTF8` (line 262-267): `ReadByte()` at EOF throws `EndOfStreamException`; an unterminated string in a truncated file is a crash, not an empty string.
- `ReadVarLen` (line 77-89): no iteration cap; a run of `0x80` bytes spins, and once `shift >= 32` C# masks the shift count to 5 bits, silently producing wrong values. `ReadBytes(uint)` (line 209-212) does `(int)num` with no range check.
- `HasBytes` (line 47-50) is arithmetically correct but overflows for large `count` and returns `true` for `count == 0`.
- `ReadCString` (line 179-193) uses `Reader.ReadBytes(num)`, which returns **fewer** bytes at EOF, so a short read silently yields a short string rather than an error — the caller cannot tell.

**Fix:** a `Require(int n)` helper that throws a typed `IoFormatException` when fewer than `n` bytes remain, called from every read; cap `ReadVarLen` at 5 iterations.

---

## 3. Cache / lifetime / load-order

### F-20 · `Content.Init` and `FAR3Provider.Init` latch their "initialised" flag before doing the work
**`FreeSO/TSOClient/tso.content/Content.cs:277`; `FreeSO/TSOClient/tso.content/Framework/FAR3Provider.cs:157-159`** · severity **High** · confidence **High**

```csharp
// Content.cs
275: private void Init() {
277:     Inited = true;          // set first
…
// Content.cs:34
34: if (INSTANCE != null) { if (!INSTANCE.Inited) INSTANCE.Init(); return; }

// FAR3Provider.cs
157: if (Initialized) return;
158: Initialized = true;        // set first
159: Cache = new TimedReferenceCache<ulong, T>();
```

Any exception during init — one corrupt `Global.far`, one unreachable `Downloads` folder — leaves the flag set and the object **permanently, silently uninitialised**. `Content.Init` will never retry, and the player gets a half-loaded game with no error. `FAR3Provider` is worse: `m_FarFiles` may be empty, and `Init` can never be called again, so the provider is empty for the session. `InitWorld()` (Content.cs:189-231) has no guard at all, so a second call re-runs every provider's `Init` and silently discards their caches.

**Fix:** set the flag in a `try { … } catch { Inited = false; throw; }`, or set it at the end. Give `InitWorld` an idempotence flag of its own.

---

### F-21 · `FileProvider` locks a field it just replaced, holds the lock across disk IO, and resolves duplicate names by enumeration order
**`FreeSO/TSOClient/tso.content/Framework/FileProvider.cs:46-51, 89-90, 100-124`** · severity **Medium-High** · confidence **High**

```csharp
46: this.Items  = new List<FileContentReference<T>>();
47: this.Cache  = new Dictionary<string, T>();      // new object…
51: lock (Cache)                                   // …so this monitors the new one
…
89: EntriesByName[name] = file;                     // last write wins
90: Items.Add(new FileContentReference<T>(name, Path.Combine(basePath, file), this));
…
103: lock (Cache) {                                // …while Get() may be locking the old one
113:     using (var reader = File.OpenRead(fullPath)) { … decode … }   // IO under the lock
```

`Init()` replaces `Cache` *before* taking the monitor on it, so the lock excludes nothing against a concurrent `Get` that already read the old field. The whole `lock` block in `Get` spans the file open, read, decode and dictionary insert, so every content load serialises globally behind disk IO.

`EntriesByName[name] = file` silently overwrites on a duplicate name while `Items` accumulates **both** references. `GetEntriesForExtension` therefore returns an entry whose `Filename` is a file the name no longer resolves to, and which `entry.Get()` will read from a *different* path than the one enumerated. Which of two same-named files wins is filesystem enumeration order — a load-order assumption, not a policy. `FAR1Provider` (line 197) and `FAR3Provider` (line 198) have the same last-wins shape; `FAR3Provider` and `AbstractObjectProvider` line 133 use `Add` and will *throw* on a duplicate GUID.

**Fix:** use a dedicated `readonly object _gate` and never reassign it; move the IO outside the lock and use `GetOrAdd` semantics or a per-key lock; log a warning on a duplicate name instead of silently overwriting.

---

### F-22 · `RCMeshProvider` — the user `MeshReplace` override is honoured in one path and ignored in three others
**`FreeSO/TSOClient/tso.content/RCMeshProvider.cs:29-30, 59, 65-69, 196, 224-225, 245`** · severity **Medium-High** · confidence **High**

```csharp
29:  if (Directory.Exists(userrepl)) repldir = userrepl;
30:  ReplaceDir = repldir;                       // user override recorded…
59:  var repldir = Path.Combine(FSOEnvironment.ContentDir, "MeshReplace/");   // …and recomputed from ContentDir
```

The constructor resolves the override into `ReplaceDir`, and `GetAnimated` (line 169) honours it. But `Get(DGRP, OBJD)` (line 59), `Replace` (line 225) and `GetTex` (line 245) all recompute `ContentDir/MeshReplace`. `ReplaceFiles` is populated from the *user* folder (line 40) while `Get` then looks in `ContentDir` — so a user with a `MeshReplace` folder gets `ReplaceFiles.Contains(name) == true`, a failed `FileStream` open, a swallowed `catch (Exception) { result = null; }` (line 71), and silent fallback to the RC path. Silent, and every one of those five catch blocks has no log.

**Fix:** use `ReplaceDir` everywhere; delete the local `repldir` recomputations at lines 59, 196 and 225.

### F-23 · `RCMeshProvider` — four unsynchronised collections and three permanently-poisoned null cache entries
**`FreeSO/TSOClient/tso.content/RCMeshProvider.cs:42, 45, 61, 114-116, 197-208, 237-260`** · severity **Medium** · confidence **High**

`CacheFiles`/`ReplaceFiles` (`HashSet<string>`), `Cache`/`NameCache` (`Dictionary<…>`) are read and written from `Get`/`Replace` with no lock, while the same class correctly locks `_animLock` for `AnimCache`/`AnimFrameCache`/`AnimGpu`. A `HashSet` mutated concurrently can corrupt its bucket chain and spin. The author's own comment at line 236 ("Could have load the same texture multiple times due to a race condition?") acknowledges it.

Two cache-key bugs: `GetTex` (line 237 vs 260) looks up `name` but stores under the **stripped** name (`name = name.Substring(4)` at line 243), so `GetTex("FSO_x")` misses forever and re-reads the file on every call; and `NameCache[name] = result` (line 208) / `Cache[dgrp] = result` (line 116) store **`null`** when every load path failed, so a mesh that is temporarily unavailable is cached as absent for the rest of the session — `UILotControl` line 1314's `Content.Get().RCMeshes.Get("arrow.fsom")` will never recover. `ClearCache`/`Replace` both carry `//todo: dispose old?` — GPU resources are leaked with no eviction.

**Fix:** key `GetTex` on the original name; do not cache negative results (or cache them with a short TTL); lock or `ConcurrentDictionary` the four collections; dispose replaced meshes.

### F-24 · `EstimateTypeId()` reads the first entry of a `Dictionary`
**`FreeSO/TSOClient/tso.content/Framework/FAR3Provider.cs:98-103`** · severity **Medium** · confidence **High**

```csharp
101: var id = EntriesById.FirstOrDefault().Key;
102: return (uint)id; // low part is ID
```

`Dictionary` enumeration order is unspecified and changes with insertion/resize history, so this returns a different value for the same archive depending on how it was built. On an empty provider it returns `0` silently. The comment claims it is "the first type id in the file", which it is not. **Fix:** track a `FirstTypeID` field while building the index in `Init`, and return that.

### F-25 · Archives and their file handles are never released
**`FreeSO/TSOClient/tso.files/FAR1/FAR1Archive.cs:44`; `FreeSO/TSOClient/tso.content/Framework/FAR1Provider.cs:185`; `FreeSO/TSOClient/tso.files/FAR3/FAR3Archive.cs:83-84`; `FreeSO/TSOClient/tso.content/Content.cs:314, 399-411`** · severity **Medium** · confidence **High**

`FAR1Archive` opens a `FileStream` in its constructor and is not `IDisposable`; `FAR1Provider.Init` creates one per `.far` and never calls `Close()`. `FAR3Archive` *is* `IDisposable`, with the comment "Keep the stream open, it helps peformance", and **nothing ever calls `Dispose`** — not `FAR3Provider.Init` (line 181), not `Content.Archives` (line 404-406). `FAR1Archive`/`DBPFFile` (`F-16`) leak too. On a full TS1 install that is hundreds of permanently open handles. `Content.GetResource`'s `path.EndsWith(".dat")` (line 399) is also case-sensitive.

**Fix:** have the providers own the archives in a list and dispose them in a `Dispose()`/`Uninit()`; add `FAR1Archive : IDisposable`; dispose `DBPFFile`'s stream.

### F-26 · `Content._ScanFiles` — unguarded recursive directory walk, and an empty `catch`
**`FreeSO/TSOClient/tso.content/Content.cs:265, 355-377`** · severity **Medium** · confidence **High**

```csharp
265: catch { }                                        // reading Content/version
…
366: var files = Directory.GetFiles(fullPath);
369:     fileList.Add(file.Substring(baseDir.Length));
372: var dirs = Directory.GetDirectories(fullPath);
375:     _ScanFiles(subDir, fileList, baseDir);
```

Neither enumeration is guarded. A single unreadable subdirectory anywhere in the install (an ACL, a cloud-storage placeholder, a dangling reparse point) throws out of the middle of the recursion and aborts content init with the window already up. `file.Substring(baseDir.Length)` throws `ArgumentOutOfRangeException` if the two separator styles ever disagree. Line 265's `catch { }` is the empty-catch anti-pattern with no log at all.

**Fix:** wrap each `GetFiles`/`GetDirectories` in a try/catch that logs and returns; guard the `Substring` with a length check.

### F-27 · `ChangeManager.Invoke` deadlocks forever if the invoked delegate throws
**`FreeSO/TSOClient/tso.content/ChangeManager.cs:26, 199-208`** · severity **Medium-High** · confidence **High**

```csharp
 26: inv.Sync.WaitOne();                              // no timeout
…
199: lock (InvokeQueue) {
201:     while (InvokeQueue.Count > 0) {
203:         var inv = InvokeQueue.Dequeue();
205:         inv.Function.DynamicInvoke(inv.Args);       // throws here…
206:         inv.Sync.Set();                             // …so this never runs
```

Any exception from the delegate — the normal case when a queued resource modification hits a missing file — leaves the calling thread blocked on `WaitOne()` with no timeout, and `RunResModifications` has already dequeued the element so a retry cannot pick it up. The calling thread hangs permanently while holding whatever it was doing. `Sync` is also never disposed.

**Fix:** `try { inv.Function.DynamicInvoke(inv.Args); } catch (Exception ex) { SimitoneLog/Console.WriteLine(ex); } finally { inv.Sync.Set(); }`, and use `WaitOne(timeout)` with a logged failure on the caller side.

### F-28 · Case-sensitivity is inconsistent across the three content providers
**`FreeSO/TSOClient/tso.content/Framework/FAR1Provider.cs:89, 197`; `FAR3Provider.cs:124, 198`; `FileProvider.cs:102, 65`** · severity **Low-Medium** · confidence **High**

`FAR3Provider` and `FileProvider` lowercase the lookup key and the stored key; `FAR1Provider` does neither, so `Get(string)` is **case-sensitive**. TS1 object names reach these providers from OBJD `FileName` fields written by hand and from mods, and `TS1ObjectProvider.GenerateResource` line 153 compensates by calling `reference.FileName.ToLower()`. One provider resolving a name that its two siblings reject is a latent content bug that only shows up for mixed-case content. `FAR3Provider.Get` (line 124) and `FileProvider.Get` (line 102) also NRE on a null name. **Fix:** lowercase in `FAR1Provider` too, and null-guard all three.

---

## 4. Bad practices

| id | location | issue | severity | fix | conf |
|---|---|---|---|---|---|
| F-29 | `TS1NeighbourProvider.cs:570,582,586`; `AbstractObjectProvider.cs:51,79` | `Console.WriteLine` for save and content failures. `SimitoneLog` exists and is used elsewhere in the same call chains, so these are **no log at all** in a windowed app. | High | `SimitoneLog.Warn` / `.Error` | High |
| F-30 | `LotLifecycleService.cs:240`; `TS1NeighbourProvider.cs:271,795`; `RCMeshProvider.cs:36,71,84,99,141,172,204,256`; `TS1GlobalTuning.cs:307`; `PIFFRegistry.cs:33`; `ImageLoader.cs:330,350`; `OBJD.cs:553`; `Track.cs:104`; `Content.cs:265` | 15+ `catch (Exception)` / empty `catch` with no log around file IO. The only two *deliberate* swallowings (`IsOpenableNeighborhood`, `IsAnimatedFile`) are documented; the rest are not. | Medium | log via `SimitoneLog`; narrow the catch to what is actually recoverable | High |
| F-31 | `FSOEnvironment.cs:10`; `Program.cs:31,62` | `UserDir` defaults to the **relative** `"Content/"`, and `Program.cs:31` explicitly sets the CWD to the install folder. Every save path is `Path.Combine(UserDir, …)`, so if `Main` line 62 has not run (or runs late, as the comment at 57-61 admits it nearly did) a save resolves to `<install>/Content/UserData/Houses/House01.iff` — or, worse, into the retail tree. | High | default `UserDir` to `Path.Combine(Environment.GetFolderPath(MyDocuments), "Simitone")`; assert rootedness in `AtomicFileCommit.Write` | High |
| F-32 | `Content.cs:74, 89` | `public static string TS1HybridBasePath = "D:/Games/The Sims/"` — a hard-coded absolute path as a field default, snapshotted into the instance field `TS1BasePath` at construction. A `Content` built before `GlobalSettings.Default.TS1HybridPath` is assigned silently uses it. | Medium | make `TS1BasePath` a property that reads the live setting, with `null` (not a path) as the unset state | High |
| F-33 | `LotLifecycleService.cs:116, 133`; `TS1NeighbourProvider.cs:131, 600, 605, 336, 399`; `Program.cs:78` | Culture-sensitive or unguarded parsing on save paths: `path.EndsWith(".iff")` (case-sensitive, NRE on null), `short.Parse` with no `NumberStyles`/`CultureInfo`, `short.Parse(split[0])` with no `TryParse` and `split[1]` unchecked, `id.ToString().PadLeft(2,'0')` without `InvariantCulture` (note `TravelSave.SlotPathFor` *does* use it — the two must agree and are written differently), `.Replace(".iff","")` replacing all occurrences, `arg[0]` on an empty command-line argument. | Medium | `InvariantCulture` + `NumberStyles.None` everywhere a save path is built; `StringComparison.OrdinalIgnoreCase` for extensions; `TryParse` throughout; trim the trailing extension by length | High |
| F-34 | `RCMeshProvider.cs:39-40`; `WorldObjectProvider.cs:77`; `TS1NeighbourProvider.cs:303,308`; `Content.cs:366,372` | `Directory.GetFiles`/`GetDirectories` used outside the guard that was supposed to cover them (in `RCMeshProvider` only the `CreateDirectory` calls are inside the `try`). | Medium | enumerate inside a try/catch that logs the directory and yields an empty set | High |
| F-35 | `TS1NeighbourProvider.cs:32, 121-135`; `TS1ObjectProvider.cs:101-119, 153-154, 16-17`; `PIFF.cs:10`; `IffFile.cs:21-24` | Mutable `static` content state: `UserDataFolderName`/templates are fine, but `TS1ObjectProvider.ItemsByCategory`/`ItemsByGUID` are `static` on an *instance* provider, `PIFF.CURRENT_VERSION` and `IffFile.RETAIN_CHUNK_DATA`/`TargetTS1` are public mutable statics, and `Content.FailedContentFiles` is a public static list. `IffFile.InitHash` (line 185) also does `chunk.ChunkData ?? chunk.OriginalData, chunk.ChunkData.Length`, which NREs when only `OriginalData` is set. | Medium | make them instance fields behind an explicit `Configure()`; fix `InitHash` to use the same expression for both | High |

---

## 5. Test effectiveness

The suite is genuinely strong on the Simitone-side *logic* — truncated-operand tests, invariant-culture tests, containment tests and commit-contract tests are everywhere, and `TS1AvatarSavePathTests` is a model of pinning a contract rather than an implementation. The weaknesses are concentrated in two places: **tests that cannot fail**, and **the complete absence of coverage for the file-format layer**.

### Tests that cannot fail, or that assert the implementation

**T-01 · `Client/Simitone/Simitone.Tests/UI/SaveServiceTests.cs:142-161`** — severity **Medium**
`HasSaveState_WhateverCombinationOfCollaboratorsIsMissing` is a byte-for-byte duplicate of `HasSaveState_IsFalse_WithNoLotLoadedAtAll` (both assert `HasSaveState(null, null, null)` is false). Its own comment says it exists "so the conjunction cannot quietly lose a term" — but it only ever passes nulls, so it cannot detect the loss of *any* term except the first. `HasSaveState_TakesTheLiveCollaboratorsRatherThanOwningThem` (line 152) claims to prove the guard is "a plain conjunction, not a 'have I been initialised' flag" but the method is `static`; no test of it can ever observe a cached instance answer. **Fix:** delete both, or drive the real combinations with non-null stand-ins (`HasSaveState(null, world, family)`, etc.) — the `World` cannot be built headlessly, so at minimum assert the guard is a pure function of its three arguments via a truth table over a substitute.

**T-02 · `SaveCompatTests.cs:394-417`** — severity **Medium**
```csharp
408: for (int i = SimiGlobalsOnWire; i < simi.GlobalData.Length; i++)
412:     Assert.Equal((short)0, simi.GlobalData[i]);
```
`SIMI.Read` allocates `GlobalData = new short[38]` (line 40), which is zero-filled by construction, so slots 32-37 are 0 for *any* readable SIMI. The loop cannot fail and tells us nothing about `Write`. **Fix:** assert the *file* — read the 32 on-the-wire shorts out of the serialised bytes and compare them to the source array, which is what actually pins the format.

**T-03 · `LotEvictionTests.cs:370-376`** — severity **Medium**
```csharp
375: Assert.Equal(0x3Eu, LotEviction.NormalizedSimiVersion);
```
A constant compared against a literal — it fails only if someone edits the constant, and it exercises no code. Its comment ("SIMI.Write writes 0x3E but takes its item count from the instance's Version, so any other value produces a chunk that disagrees with itself") correctly diagnoses **F-02** and then tests nothing about it. **Fix:** build a SIMI with `Version = 0x40`, write it, read it back, and assert `LotValue`/`ObjectsValue`/`ArchitectureValue` and the first 32 globals survive — that is the contract the comment is describing.

**T-04 · `NeighborhoodServiceTests.cs:238`** — severity **Low**
`Assert.NotNull(simi.BudgetDays)` cannot fail: `SIMI.Read` line 72 always allocates `new SIMIBudgetDay[6]`. Combined with `SyntheticSimi()` (line 427-429) building six all-zero budget days, `SIMI.Write` returns at line 156 and the `MiscIncome`/`BillsExpense` swap at **F-01** is never executed by any test in the repository.

**T-05 · no coverage of the buggy branch at all** — severity **High** (this is the gap that let F-01 ship)
Zero tests anywhere set `SIMIBudgetDay.Valid != 0` or non-zero expense fields. `grep -r "BudgetDays|BillsExpense|SIMIBudgetDay"` across the whole repo returns only production code and all-zero fixtures. **Fix:** one `[Fact]` — six `SIMIBudgetDay`s with all nine fields set to distinct values and `Valid = 1`, `SIMI.Write` → `IffFile.Write` → `IffFile.Read`, asserting field-for-field equality. That single test closes F-01 and F-02's array half.

**T-06 · `TravelSaveTests.cs:96`** — severity **Low**
`Assert.Equal(expected.Substring(0, 7), Path.GetFileName(slot).Substring(0, 7))` compares `"House12"` with `"House12"` — both sides are literals derived from the same `houseId` argument. It cannot fail and asserts nothing about the code.

**T-07 · `TravelSaveTests.cs:85-97` claims an invariant it does not test** — severity **Low**
`SingleDigitLotNumbersArePaddedToTwoJustLikeTheHouseFiles` asserts that `TravelSave.SlotPathFor` agrees with `TS1NeighbourProvider.GetHousePath` — the entire point of the test. But the expected value is built with the *same* `houseId.ToString().PadLeft(2, '0')` expression, while `SlotPathFor` uses `houseId.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0')` (`TravelSave.cs:117`) and `GetHousePath` uses the culture-sensitive form (`TS1NeighbourProvider.cs:605`). The test would **not** detect a divergence between those two, and it never calls `GetHousePath`. **Fix:** call `TS1NeighborhoodProvider.GetHousePath` with a stubbed `UserPath` and compare the file stems, or delete the claim.

**T-08 · `SaveServiceTests.cs:340-356`** — severity **Low**
`BuildHouseFileBytes_IsStableForTheSameInput`'s own comment concedes "the previous save is compared byte for byte by nothing". It pins a property nothing relies on and would not surface non-determinism anywhere observed. Either drop it or give it a consumer.

**T-09 · `AtomicFileCommitTests.cs:194-205` and `FaultInjectionTests.cs:29-45`** — severity **Medium**
Both assert only `Assert.NotNull(thrown)` / `Assert.ThrowsAny<Exception>` for "a directory is in the way". Any exception satisfies them, so a change that made the commit fail for an unrelated reason would still pass. Neither asserts what the *contract* says should survive — that the directory is untouched **and** what state the `.tmp`/`.bak` are left in. The implementation in fact leaves a complete `.tmp` behind in this case (step 2 succeeds, step 4 throws), and that is worth pinning explicitly.

**T-10 · `AtomicFileCommit` has no failure-injection test, so F-04 is structurally untestable** — severity **High**
Nothing in the suite can make step 4 (`File.Copy(tmp, path, true)`) fail part way through, so the class's central claim — that a reader can never observe a truncated file — is **asserted only in the doc comment** and pinned by nothing. `AtomicFileCommit` takes `path` and `bytes` as concrete values with no seam, so a test cannot reach it. **Fix:** extract the commit step behind an injectable `Action<string,string,bool>` (or a `FileOps` abstraction) so a test can throw mid-copy and assert the live file is still the complete previous save. Until then the guarantee is documentation, not code.

### Missing edge cases for the parsers

**T-11 · severity High — the file-format layer has essentially no tests.** Several suites construct an `IffFile` programmatically to get at Simitone-side logic (`SaveServiceTests`, `SaveCompatTests`, `LotZoningTests`, `LotZoningWriteFormatTests`, `NeighborhoodServiceTests`, `TS1AvatarSavePathTests`), and two of them read one back off disk — but every one of them feeds `IffFile` a file that `IffFile` itself just wrote. The *reader* is only ever exercised against three hand-made shapes in `SaveCompatTests`: empty file, 4-byte non-IFF, and missing file. There is **no** test for:
- `IffFile.Read` with `chunkSize == 0` or `chunkSize < 76` (F-10), with a chunk size past EOF, with a truncated tail mid-payload, with a non-ASCII chunk label, with a 2.5-vs-2.0 header, or with an unknown chunk type at the end of the file (the `Skip` path at line 204).
- `IoBuffer` — no test at all. `ReadVarLen`, `ReadLongPascalString`, `ReadNullTerminatedString`, `ReadCString` short reads, and the `HasBytes` boundary are all unpinned (F-19).
- `FAR1Archive` / `FAR3Archive` / `Decompresser` / `DBPFFile` — **no test at all.** The FAR3 endianness bug (F-11) would have been caught by one `Compress`→`Decompress` round trip.
- `PiffEncoder` / `PIFF` / `PIFFEntry.Apply` — no test at all (F-17, F-18). A `GeneratePiff` → `Write` → `Read` → `Apply` round trip is the obvious missing test.
- `NGBH`, `NBRS`, `FAMI`, `HOUS`, `OBJD`, `TTAB`, `STR`, `SPR2` — no chunk-level round-trip or malformed-input tests. `NGBH.Read` in particular (line 39-52) trusts a file-supplied `count` and `inventoryCount` with no bounds check and does `io.ReadInt32() != 1` as a bare statement, and `InventoryByID[neighID] = inventory` silently overwrites a duplicate neighbourhood id.
- `VMMarshal.Deserialize` — no test with a negative count, an `int.MaxValue` count, or a truncated payload (F-09).

**T-12 · severity Medium — `SunCycleSettingTests` mutates process-global state outside the non-parallel collection.** `UI/SunCycleSettingTests.cs:156,178` assigns `FSOEnvironment.UserDir`, and 151/171/180 assign `SunCycleSetting.Enabled` and `WorldConfig.Current.SunCycle` — all process-wide. `NonParallelCollection.cs:16-22` documents at length that exactly this pattern has already produced an intermittent failure (`Log_IsRotatedOnceItReachesTwoMebibytes` "held four lines instead of one"), and four other classes that touch the same globals (`SimitoneLogTests`, `NeighbourhoodManagerTests`, `VMEODDiagnosticsTests`, `LotZoningWriteFormatTests`) are all in the collection. `SunCycleSettingTests` is not. xUnit runs test *classes* in parallel by default and there is no `xunit.runner.json` and no assembly-level `[CollectionBehavior]`, so this is a live flake. **Fix:** add `[Collection(NonParallelCollection.Name)]` to the class, and add a guard test that asserts every class touching `FSOEnvironment.UserDir` declares the collection.

**T-13 · severity Medium — the durability of the actual neighbourhood save is untested.** `SaveCompatTests` is explicit that it tests `AtomicFileCommit` and the house-file envelope. Nothing tests `TS1NeighborhoodProvider.SaveNeighbourhood`, `LoadCharacters` (including the `return`/`continue` bug at F-05), `SaveAvatarIff`'s overwrite, or the first-run `CopyFolder`. The highest-value file in the save (F-03) has zero coverage, and the two most obviously broken behaviours in the persistence layer (F-05, F-06) are the two most obviously testable — both need only a temp directory with two `.iff` files and a `.txt`.

**T-14 · severity Low — no test reads a real TS1 install, by design.** That is the right call for CI, but it means every finding above that depends on real-world archive shapes (F-11, F-14, F-16, the duplicate-key cases) is invisible to the build. A checked-in 2 KB synthetic `.far3`/`.far1`/`.piff` fixture pair (one well-formed, one deliberately malformed) would close most of that gap at negligible cost, and is the prerequisite for T-11.

---

## 6. Suggested order of work

1. **F-01, F-02** — two one-line fixes in `SIMI.cs` that stop silent corruption of a player's own lot save, plus **T-05** so they cannot come back.
2. **F-03, F-04** — route `Neighborhood.iff` through `AtomicFileCommit`, and make `AtomicFileCommit` actually atomic with `File.Replace`. Add the failure-injection seam (**T-10**) and the `SaveNeighbourhood` durability test (**T-13**).
3. **F-05, F-06** — `return` → `continue`, and reuse the already-hardened `NeighbourhoodManager` copy. Both are cheap and both are on the first-run path where a failure is unrecoverable.
4. **F-12, F-11, F-13, F-14** — the parser and cache defects with process-wide or unbounded blast radius. F-12 in particular is a one-line `try/finally` that removes a permanent-poisoning failure mode.
5. **F-09, F-10, F-19** — add `Require(n)` to `IoBuffer` and bounds checks to the count reads in `VMMarshal` and `IffFile`; add the parser tests in **T-11** alongside.
6. **T-01 – T-03, T-12** — delete the three tests that cannot fail, add `SunCycleSettingTests` to the non-parallel collection.
7. Everything in §3 and §4.

**Overall.** The save/persistence *design* is unusually good — `AtomicFileCommit`, `TravelSave`, `LotPersistence`, `TS1AvatarSavePath` and `NeighbourhoodLayoutStore` are well-specified, well-commented and thoughtfully guarded, and the test suite around them is genuinely strong. The problems are concentrated where the good Simitone-side abstractions meet the inherited FreeSO code: the strongest durability contract in the codebase is applied to the *least* valuable file, one of the two commit paths doesn't honour its own contract, and the binary parsers that everything else rests on have no bounds checks and no tests.
