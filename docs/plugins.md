# Writing a backend plugin

The gateway's backends are **providers**: named implementations that fill one or more
**roles** (`MailStore`, `MailSubmit`, `Calendar`, `Tasks`, `Contacts`, `Notes`, `Oof`).
The in-repo providers (`imap`, `smtp`, `caldav`, `carddav`, `sieve`, `local`) are ordinary
providers registered at startup; an **out-of-repo plugin** is the same thing shipped as a
separate assembly the gateway loads from a directory. Nothing about a plugin provider is
second-class — config assigns it to a role by name exactly like a built-in.

This is the seam JMAP will land on, and the one you use to add a backend the project
doesn't ship.

## The contract

Reference three NuGet packages (published per release to GitHub Packages, and nuget.org
when configured) — pin the **exact minor** you target (see *Versioning* below):

- `ActiveSync.Protocol` — EAS/WBXML primitives.
- `ActiveSync.Core` — the provider contract (`IBackendProvider`, `IContentStore`,
  `IGatewayPlugin`), options and account model.
- `ActiveSync.Backends.Common` — optional; the MIME/iCalendar/vCard converters and
  TLS/wire-logging helpers, if your provider speaks those formats.

A plugin assembly contains:

1. One or more **`IBackendProvider`** implementations — the actual backend.
2. One **`IGatewayPlugin`** implementation — the entry point that registers them.

```csharp
using ActiveSync.Core.Backend;
using ActiveSync.Core.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public sealed class MyPlugin : IGatewayPlugin
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Register like an in-repo provider; anything it depends on can be registered too.
        services.AddSingleton<IBackendProvider, MyNotesProvider>();
    }
}

public sealed class MyNotesProvider : IBackendProvider
{
    public string Name => "my-notes";                       // what config's Provider names
    public IReadOnlySet<BackendRole> SupportedRoles { get; } =
        new HashSet<BackendRole> { BackendRole.Notes };

    public void ValidateConfiguration(BackendRole role, ProviderSettings settings, IList<string> failures)
    {
        // Bind your OWN options type — the host never knows its shape.
        MyOptions options = settings.Bind<MyOptions>();
        if (string.IsNullOrWhiteSpace(options.Endpoint))
            failures.Add($"my-notes ({role}): Endpoint is required.");
    }

    public string DescribeRole(BackendRole role, ProviderSettings settings) =>
        $"my-notes {settings.Bind<MyOptions>().Endpoint}";   // one redacted banner line, no secrets

    public IBackendConnection CreateConnection(BackendConnectionContext context)
    {
        // Build one IContentStore per content role assigned to you, over one connection.
        // context.GatewayCredentials is the IDENTITY (DB scoping, cache keys); each role's
        // Credentials are what to present to the backend.
        MyOptions options = context.Roles[0].Settings.Bind<MyOptions>();
        return new BackendConnection([new MyNotesStore(options, context.Roles[0].Credentials)]);
    }
}
```

Key rules:

- **`Name`** is the discriminator config uses (`"Provider": "my-notes"`). It must be
  unique across all providers — a collision fails startup.
- **Bind your own options** from `ProviderSettings` inside your provider. The host passes
  the raw role section through; it deliberately cannot see your option type. That is what
  lets a plugin carry configuration the host was never compiled against.
- **`IContentStore.OwnsBackendKey`** must claim a key space disjoint from every other
  store in a session (the built-ins use `imap:`, `caldav:`, `carddav:`, `local:` — pick
  your own prefix). The session dispatches folder/item keys to the first store that
  claims them.
- **Optional capabilities** are extra interfaces a store/provider may also implement:
  `ICalendarOperations`, `IFreeBusySource`, `ICalendarAttachmentSource`,
  `IContactOperations`, `IReadOnlyCollectionSource` (on stores); `ICredentialVerifier`
  (verify a login for the auth path — required if your provider serves `MailStore` for
  pass-through users), `IPerUserResourceOwner` (trim per-user caches on session eviction),
  `IReadinessSource` (a `/readyz` probe). Implement only what applies.
- **The gateway login is the identity** — DB row scoping, the local-content encryption
  AAD, and session/cache keys all derive from `context.GatewayCredentials.UserName`, never
  a per-backend user name.

## Configuration

A plugin provider is assigned to a role like any other, by name:

```json
"ActiveSync": {
  "Backends": {
    "Notes": { "Provider": "my-notes", "Endpoint": "https://notes.example.com" }
  }
}
```

`Endpoint` (and anything else in the section) is bound by your provider, not the host.

## Packaging and deployment

Build your plugin as a normal class library targeting the same framework as the gateway
(`net10.0`). The gateway loads plugins from a **plugins directory**
(`ActiveSync:Plugins:Directory`, default `/app/plugins` in the container image): one
**subdirectory per plugin**, whose entry assembly is named after the subdirectory, with
any private dependencies beside it.

```
/app/plugins/
  my-notes/
    my-notes.dll          <- entry assembly (matches the directory name)
    SomePrivateDep.dll     <- private dependencies, if any
```

Do **not** ship copies of `ActiveSync.Core`/`Protocol`/`Backends.Common` or the framework
in your plugin directory — the loader resolves those from the host so your types unify
with the gateway's (a private copy would make `IBackendProvider` a different type and the
provider would be ignored). Mark those package references `<Private>false</Private>` (or
`ExcludeAssets="runtime"`).

Two ways to get the directory populated:

- **Derived image** (immutable, the documented default):

  ```dockerfile
  FROM ghcr.io/sterion/active-sync-gateway:latest
  COPY my-notes/ /app/plugins/my-notes/
  ```

- **Volume mount** (update without rebuilding): mount your plugin into `/app/plugins`
  (a k8s `volume` / initContainer that drops the DLLs, or `-v ./plugins:/app/plugins`).

The image is **multi-arch** (`linux/amd64` + `linux/arm64`). A pure-managed plugin runs on
both as-is; a plugin with a native dependency must ship both RIDs.

## Loading behavior

- Each plugin loads in its own `AssemblyLoadContext`; the shared contract and framework
  assemblies resolve from the host (type identity), private deps from the plugin folder.
- Loading is **fail-fast**: a corrupt/incompatible plugin, a subdirectory whose entry
  assembly is missing, or an assembly with no `IGatewayPlugin` aborts startup rather than
  silently degrading a role that config assigned to it. An absent or empty plugins
  directory is a no-op.
- Each loaded provider appears on the startup banner via its role line.

## Versioning

The backend contract is **not ABI-stable before 2.0** — `IContentStore` and friends still
evolve with new EAS features. The loader enforces that a plugin's referenced
`ActiveSync.Core` **major** version matches the host and aborts on a mismatch. Pin the
exact minor you built against and rebuild your plugin when you upgrade the gateway across a
minor, until a 2.0 stability guarantee lands.
