# Vintage-DGLab project

The mod targets .NET 10 and the real Vintage Story API version specified by
`SupportedGameVersion` in `CoyoteMod.csproj`. Runtime code is in `src/v13/`.
The unified release uses the real 1.22.3 API and supports game versions
1.22.3 through 1.22.7 with the same ZIP. The upper declared version is recorded
as `SupportedGameVersionMax`; see the validation record for the tested scope.

See the repository [README](../README.md), [build instructions](../BUILDING.md),
[validation record](../VALIDATION.md), and [third-party notices](../THIRD_PARTY_NOTICES.md).

The native GUI is opened with **Ctrl+Alt+G** or the client chat command **`.dglab`**.
There is no sensor block, nearest-player tracking, or external GUI requirement.
