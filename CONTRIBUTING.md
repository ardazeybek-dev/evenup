# Contributing

Bug reports and pull requests are welcome.

## Development loop

```bash
dotnet build
dotnet test
dotnet run --project src/Evenup -- settle examples/lisbon-trip.evenup
dotnet run --project src/Evenup -- serve        # playground on http://127.0.0.1:5317
```

## Ground rules

- **Money stays integral.** Store amounts as `int64` minor units. Use `Money.allocate` whenever an amount is divided, and never round a share by hand.
- **Culture-invariant everywhere.** Pass `CultureInfo.InvariantCulture` to every parse and format. Compare keywords with `StringComparison.OrdinalIgnoreCase`, never `ToLower()`/`ToUpper()`.
- **F# compile order matters.** A new file must go into the `.fsproj` *after* every file it depends on.
- **The core stays pure.** `Evenup.Core` must not do I/O. Files, HTTP and the console belong in `src/Evenup`.
- **Add a test for every bug fix.** If the bug is about settlement or splitting, prefer a property test.
- **New syntax needs docs.** Any change to the ledger syntax has to update `docs/format.md` and, where it makes sense, an example in `examples/`.

## Commit style

Use short, imperative subject lines, for example "Add percent split validation".
