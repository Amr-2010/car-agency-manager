# Build fix v0.6

Fixed CS1597 in src/CarAgency.Api/Models.cs by removing the stray semicolon after ImageUrl accessor.

NU1902/NU1903: System.Security.Cryptography.Pkcs 9.0.0 is a transitive dependency warning. Run `dotnet list src/CarAgency.Api/CarAgency.Api.csproj package --include-transitive` and update the responsible dependency to a patched compatible version; do not suppress security warnings.

Not compiled or deployed in this environment.
