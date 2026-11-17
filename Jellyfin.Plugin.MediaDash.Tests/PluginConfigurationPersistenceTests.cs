using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Plugin.MediaDash.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

[Collection("MediaDashDb")]
public sealed class PluginConfigurationPersistenceTests
{
    [Fact]
    public void FreshPluginAndLegacyPluginReloadKeepSettingsAndRequireApproval()
    {
        var root = Path.Combine(Path.GetTempPath(), "md-config-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Plugin.Instance;
        try
        {
            var paths = DispatchProxy.Create<IApplicationPaths, OriginalLanguageFlowTests.CallProxy>();
            ((OriginalLanguageFlowTests.CallProxy)(object)paths).Call = (_, _) => root;
            var serializer = DispatchProxy.Create<IXmlSerializer, OriginalLanguageFlowTests.CallProxy>();
            var xml = new XmlSerializer(typeof(PluginConfiguration));
            string? savedPath = null;
            ((OriginalLanguageFlowTests.CallProxy)(object)serializer).Call = (method, args) =>
            {
                if (method.Name == "DeserializeFromFile")
                {
                    using var reader = File.OpenRead((string)args![1]!);
                    return xml.Deserialize(reader);
                }
                if (method.Name == "SerializeToFile")
                {
                    savedPath = (string)args![1]!;
                    using var writer = File.Create(savedPath);
                    xml.Serialize(writer, args[0]);
                    return null;
                }
                throw new InvalidOperationException(method.Name);
            };

            var fresh = new Plugin(paths, serializer);
            Assert.True(fresh.Configuration.DryRun);
            Assert.Equal(FixMode.ManualApprove, fresh.Configuration.AudioFixMode);
            Assert.Equal(FixMode.ManualApprove, fresh.Configuration.CorruptArtworkFixMode);
            fresh.Configuration.AllowedAudioLanguages = ["spa", "orig"];
            fresh.Configuration.EnabledLibraries = ["owned-library"];
            fresh.Configuration.RecycleBinPauseFixesAtGb = 0;
            fresh.Configuration.AudioDisposal = DisposalMethod.Permanent;
            fresh.Configuration.PlayabilityFixMode = FixMode.Off;
            fresh.Configuration.TranscodeFixMode = FixMode.Automatic;
            fresh.SaveConfiguration();
            Assert.NotNull(savedPath);
            var persisted = File.ReadAllText(savedPath);
            Assert.Contains("<AudioFixMode>ManualApprove</AudioFixMode>", persisted);
            File.WriteAllText(savedPath, persisted.Replace(">ManualApprove<", ">DetectOnly<", StringComparison.Ordinal));

            var upgraded = new Plugin(paths, serializer);
            Assert.Equal(FixMode.ManualApprove, upgraded.Configuration.AudioFixMode);
            Assert.DoesNotContain(">DetectOnly<", File.ReadAllText(savedPath));
            var restarted = new Plugin(paths, serializer);
            Assert.False(restarted.Configuration.NormaliseFixModes());
            Assert.Equal(new[] { "spa", "orig" }, restarted.Configuration.AllowedAudioLanguages);
            Assert.Equal(new[] { "owned-library" }, restarted.Configuration.EnabledLibraries);
            Assert.Equal(0, restarted.Configuration.RecycleBinPauseFixesAtGb);
            Assert.Equal(DisposalMethod.Permanent, restarted.Configuration.AudioDisposal);
            Assert.Equal(FixMode.Off, restarted.Configuration.PlayabilityFixMode);
            Assert.Equal(FixMode.Automatic, restarted.Configuration.TranscodeFixMode);
            Assert.True(restarted.Configuration.DryRun);
            Assert.All(typeof(PluginConfiguration).GetProperties().Where(p => p.PropertyType == typeof(FixMode)),
                property => Assert.NotEqual(1, (int)(FixMode)property.GetValue(restarted.Configuration)!));
        }
        finally
        {
            typeof(Plugin).GetProperty("Instance")!.SetValue(null, previous);
            Directory.Delete(root, true);
        }
    }
}
