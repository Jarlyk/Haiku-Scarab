using System.IO;

namespace Scarab.Interfaces
{
    public interface ISettings
    {
        bool AutoRemoveDeps { get; }
        
        string ManagedFolder { get; set; }
        
        string ModsFolder     => Path.Combine(ManagedFolder, "BepInEx/plugins");
        string DisabledFolder => Path.Combine(ModsFolder, "..","Disabled");

        void Save();
    }
}