namespace Jellyfin.Plugin.TheSportsDB;

using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.TheSportsDB.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public override string Name => "TheSportsDB";

    public override Guid Id => Guid.Parse("5fce2032-15f1-4171-4141-86105f201010");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = this.Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
