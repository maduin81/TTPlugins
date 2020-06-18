﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace com.tiberiumfusion.ttplugins.HarmonyPlugins
{
    /// <summary>
    /// Handles the application of HPlugins as Harmony patches.
    /// </summary>
    public static class HPluginApplicator
    {
        #region Vars

        /// <summary>
        /// The Harmony instance which was created during patch application
        /// </summary>
        internal static HarmonyLib.Harmony HarmonyInstance { get; private set; }

        /// <summary>
        /// A list of all HPlugins that were successfully applied.
        /// </summary>
        internal static List<HSupervisedPlugin> AppliedHPlugins { get; private set; }

        /// <summary>
        /// Dictionary that maps each HPlugin to the HSupervisedPlugin object that manages it.
        /// </summary>
        private static Dictionary<HPlugin, HSupervisedPlugin> HPluginToSupervised = new Dictionary<HPlugin, HSupervisedPlugin>();

        /// <summary>
        /// The HPluginApplicatorConfiguration used in the last ApplyPatches() call.
        /// </summary>
        private static HPluginApplicatorConfiguration LastConfiguation;

        /// <summary>
        /// List of namespaces which HPlugins are not allowed to patch.
        /// </summary>
        private static List<string> ProtectedNamespaces = new List<string>()
        {
            "System",
            "com.tiberiumfusion",
            "HarmonyLib"
        };

        #endregion


        #region Assembly Resolver

        // Fixes bizarre problems with dynamically loaded assemblies missing all their types and fields because the CLR couldnt "find" the correct assembly
        internal static void SetupDomainAssemblyResolver()
        {
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
        }
        // See: https://stackoverflow.com/questions/2658275/assembly-gettypes-reflectiontypeloadexception
        // The bizarre way this works:
        // 1. The CLR doesnt know where an Assembly is, so it gives us its full name.
        // 2. We find the loaded assembly that has that full name and give it to the CLR.
        // 3. The CLR is baffled by how well we solved this incredibly complex problem and things work again.
        internal static Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            var domain = (AppDomain)sender;

            foreach (var assembly in domain.GetAssemblies())
                if (assembly.FullName == args.Name)
                    return assembly;

            return null;
        }

        #endregion


        #region Patch Application

        /// <summary>
        /// Applies all HPlugins from the provided compiled assemblies.
        /// </summary>
        public static HPluginApplicatorResult ApplyPatches(HPluginApplicatorConfiguration configuration)
        {
            LastConfiguation = configuration;
            HPluginApplicatorResult result = new HPluginApplicatorResult();


            // Fix weird missing assembly issues
            SetupDomainAssemblyResolver();


            // Load all Terraria assemblies & dependencies into our AppDomain (will include Terraria + its extracted embedded dependencies)
            try
            {
                foreach (byte[] asmBytes in configuration.AllDependencyAssemblyBytes)
                    Assembly.Load(asmBytes);
            }
            catch (Exception e)
            {
                result.ConfigureAsFailure(HPluginApplicatorResultCodes.DependencyAssemblyLoadFailure, e);
                return result;
            }
            

            // Create a harmony instance
            try
            {
                HarmonyInstance = new HarmonyLib.Harmony("com.tiberiumfusion.ttplugins.HarmonyPlugins.HPluginApplicator");
            }
            catch (Exception e)
            {
                result.ConfigureAsFailure(HPluginApplicatorResultCodes.CreateHarmonyInstanceFailure, e);
                return result;
            }


            // Find all HPlugins in the compiled assemblies
            AppliedHPlugins = new List<HSupervisedPlugin>();
            try
            {
                foreach (byte[] usercodeAsmBytes in configuration.AllUsercodeAssemblies)
                {
                    // Try to create the assembly from its bytes
                    Assembly usercodeAsm;
                    try { usercodeAsm = Assembly.Load(usercodeAsmBytes); }
                    catch (Exception e)
                    {
                        result.ConfigureAsFailure(HPluginApplicatorResultCodes.UsercodeAssemblyLoadError, e);
                        return result;
                    }

                    // Find all HPlugins in the usercode assembly
                    List<Type> foundPluginTypes = usercodeAsm.GetTypes().Where(t => t.IsClass && t.IsSubclassOf(typeof(HPlugin))).ToList();
                    foreach (Type pluginType in foundPluginTypes)
                    {
                        // Create an instance of the plugin
                        HPlugin pluginInstance = Activator.CreateInstance(pluginType) as HPlugin;
                        // Wrap it up
                        HSupervisedPlugin supervisedPlugin = new HSupervisedPlugin(pluginInstance);
                        // And map it
                        HPluginToSupervised[pluginInstance] = supervisedPlugin;

                        // Initialize() tells the plugin to set its informational fields, which need to be established first and foremost
                        try
                        {
                            supervisedPlugin.Plugin.Initialize();
                        }
                        catch (Exception e)
                        {
                            result.HPluginsThatThrewExceptions[supervisedPlugin.SavedataIdentity] = e;
                            continue; // Skip over this plugin
                        }
                        
                        // If the plugin has persistent data, load that now
                        XDocument pluginConfigurationDoc = null;
                        if (supervisedPlugin.Plugin.HasPersistentData)
                        {
                            // Try to load the plugin configuration from the disk
                            try
                            {
                                string savedataFile = GetConfigurationXMLFilePathForPlugin(supervisedPlugin, configuration);
                                if (File.Exists(savedataFile)) // Load config if it exists
                                {
                                    pluginConfigurationDoc = XDocument.Load(savedataFile);
                                }
                            }
                            catch (Exception e)
                            {
                                result.HPluginsWithFailedConfigurationLoads[supervisedPlugin.SavedataIdentity] = e;
                            }
                        }
                        // If config didnt exist or failed to load, create a new default one
                        if (pluginConfigurationDoc == null)
                            pluginConfigurationDoc = CreateBlankPluginSavedataDoc();

                        // Setup the Configuration object from the xml doc
                        try
                        {
                            XElement savedataElement = pluginConfigurationDoc.Element("Savedata");
                            if (savedataElement != null)
                                supervisedPlugin.Plugin.Configuration.Savedata = savedataElement;
                        }
                        catch (Exception e)
                        {
                            result.HPluginsWithFailedConfigurationLoads[supervisedPlugin.SavedataIdentity] = e;
                            // In this case, the HPlugin will have a default HPluginConfiguration object with an empty savedata XElement
                        }

                        // Configure() lets the plugin know it can safely read Configuration now
                        try
                        {
                            supervisedPlugin.Plugin.Configure();
                        }
                        catch (Exception e)
                        {
                            result.HPluginsThatThrewExceptions[supervisedPlugin.SavedataIdentity] = e;
                            continue; // Skip over this plugin
                        }

                        // Now that the plugin is initialized and configured, it is finally time to patch it in with Harmony
                        // Do all the patch operations defined by the plugin (should have been done by the user in Initialize() or Configure())
                        foreach (HPatchOperation patchOp in supervisedPlugin.Plugin.PatchOperations)
                        {
                            // First validate the patchOp

                            // Ensure both the target and patch stub MethodInfos exist
                            if (patchOp.TargetMethod == null || patchOp.StubMethod == null)
                            {
                                result.HPluginsWithNullMethodInfos.Add(supervisedPlugin.SavedataIdentity);
                                continue; // Skip over this plugin
                            }

                            // Ensure the target method is not in a protected namespace
                            bool brokeRules = false;
                            foreach (string protectedNamespace in ProtectedNamespaces)
                            {
                                if (patchOp.TargetMethod.DeclaringType.Namespace.IndexOf(protectedNamespace) == 0)
                                {
                                    result.HPluginsThatBrokeRules[supervisedPlugin.SavedataIdentity] = "Tried to patch protected namespace: \"" + protectedNamespace + "\"";
                                    brokeRules = true;
                                    break;
                                }
                                if (brokeRules) break;
                            }
                            if (brokeRules)
                                continue; // Skip over this plugin


                            // At last, do the Harmony patch
                            try
                            {
                                if (patchOp.PatchLocation == HPatchLocation.Prefix)
                                    HarmonyInstance.Patch(patchOp.TargetMethod, new HarmonyLib.HarmonyMethod(patchOp.StubMethod));
                                else if (patchOp.PatchLocation == HPatchLocation.Postfix)
                                    HarmonyInstance.Patch(patchOp.TargetMethod, null, new HarmonyLib.HarmonyMethod(patchOp.StubMethod));
                            }
                            catch (Exception e)
                            {
                                result.HPluginsThatDidntPatch[supervisedPlugin.SavedataIdentity] = e;
                                // Carry on to the next plugin
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                result.ConfigureAsFailure(HPluginApplicatorResultCodes.GenericHPluginApplicationFailure, e);
                return result;
            }

            // All done
            return result;
        }

        #endregion


        #region Helpers

        /// <summary>
        /// Creates a new, blank XDocument that contains the necessary XML structure for a plugin's configuration.xml file.
        /// </summary>
        /// <returns>The completed XDocument.</returns>
        private static XDocument CreateBlankPluginSavedataDoc()
        {
            XDocument pluginConfigurationDoc = new XDocument();
            pluginConfigurationDoc.Add(new XElement("Savedata"));
            return pluginConfigurationDoc;
        }
        
        /// <summary>
        /// Gets the file path where the specified HPlugin's configuration.xml resides.
        /// </summary>
        /// <returns>The path to configuration.xml</returns>
        private static string GetConfigurationXMLFilePathForPlugin(HSupervisedPlugin supervisedPlugin, HPluginApplicatorConfiguration applicatorConfiguration)
        {
            if (applicatorConfiguration == null)
                return null;

            string savedataFolder = Path.Combine(applicatorConfiguration.RootPluginSavedataPath, supervisedPlugin.SavedataIdentity);
            Directory.CreateDirectory(savedataFolder); // Ensure directory exists
            string savedataFile = Path.Combine(savedataFolder, "configuration.xml");
            return savedataFile;
        }

        /// <summary>
        /// Asynchronously writes the specified HPlugin's Configuration property to disk. Is typically called by HPlugins when their usercode logic wants to save their Configuration's current Savedata.
        /// </summary>
        /// <param name="plugin"></param>
        internal static void WriteConfigurationForHPatch(HPlugin plugin)
        {
            Task.Run(() =>
            {
                try
                {
                    HSupervisedPlugin supervisedPlugin = HPluginToSupervised[plugin];
                    if (supervisedPlugin != null)
                    {
                        string savedataFilePath = GetConfigurationXMLFilePathForPlugin(supervisedPlugin, LastConfiguation);
                        if (savedataFilePath != null)
                        {
                            XDocument doc = new XDocument();
                            doc.Add(supervisedPlugin.Plugin.Configuration.Savedata);
                            doc.Save(savedataFilePath);
                        }
                    }
                }
                catch (Exception e) { } // Swallow it for now
            });
        }

        #endregion
    }
}