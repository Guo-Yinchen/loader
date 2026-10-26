using System.Reflection;

static class AdminAssets
{
    public static void MapAdminAssets(this WebApplication app)
    {
        foreach (var (route, resource, type) in new[] {
            ("/admin-assets/style.css", "Chispa.Admin.style.css", "text/css; charset=utf-8"),
            ("/admin-assets/app.js", "Chispa.Admin.app.js", "text/javascript; charset=utf-8"),
            ("/admin-assets/chispa.svg", "Chispa.Admin.chispa.svg", "image/svg+xml") })
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var content = reader.ReadToEnd();
            app.MapGet(route, () => Results.Content(content, type));
        }
    }
}
