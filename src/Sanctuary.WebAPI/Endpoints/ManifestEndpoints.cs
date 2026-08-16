using System.Xml.Linq;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Sanctuary.WebAPI.Endpoints;

// Serves the launcher's server manifest at GET /servermanifest.xml (the OSFRLauncher fetches
// <serverUrl>/servermanifest.xml). Values come from the optional "ServerManifest" config section
// (appsettings/gateway config) so the name/description/addresses can be edited without recompiling;
// sensible defaults are used when the section is absent.
public static class ManifestEndpoints
{
    public static void MapManifestEndpoints(this WebApplication app)
    {
        app.MapGet("/servermanifest.xml", (IConfiguration config) =>
        {
            var section = config.GetSection("ServerManifest");

            var name = section["Name"] ?? "Sul Server";
            var description = section["Description"]
                ?? "Sul's test server for special people - now with a full quest system (objectives, live tracker & \"Take Me There\" breadcrumb), collect-and-return quests, job leveling with XP, stars & full-screen level-up celebrations, working health/mana, and synced boombox dances.";
            // Fall back to the loopback address rather than a specific deployment's public IP: a server
            // that has not filled in the ServerManifest section is being run locally, and baking a remote
            // host in here silently points every launcher at someone else's box.
            var webApiUrl = section["WebApiUrl"] ?? "http://127.0.0.1:5055";
            var loginServer = section["LoginServer"] ?? "127.0.0.1:20042";

            // XElement handles XML escaping (e.g. & -> &) automatically.
            var manifest = new XElement("ServerManifest",
                new XAttribute("version", 2),
                new XElement("Name", name),
                new XElement("Description", description),
                new XElement("WebApiUrl", webApiUrl),
                new XElement("LoginServer", loginServer));

            return Results.Content(manifest.ToString(), "application/xml");
        });
    }
}
