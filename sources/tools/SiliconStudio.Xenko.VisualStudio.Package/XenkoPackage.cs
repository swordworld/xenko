﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.Build.Evaluation;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Package;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using NShader;
using SiliconStudio.Xenko.VisualStudio.BuildEngine;
using SiliconStudio.Xenko.VisualStudio.Commands;
using SiliconStudio.Xenko.VisualStudio.Shaders;

namespace SiliconStudio.Xenko.VisualStudio
{
    /// <summary>
    ///  Quick and temporary VS package to allow platform switch for Xenko.
    ///  This code needs to be largely refactored and correctly designed.
    ///  - alex
    /// 
    ///     This is the class that implements the package exposed by this assembly.
    ///     The minimum requirement for a class to be considered a valid package for Visual Studio
    ///     is to implement the IVsPackage interface and register itself with the shell.
    ///     This package uses the helper classes defined inside the Managed Package Framework (MPF)
    ///     to do it: it derives from the Package class that provides the implementation of the
    ///     IVsPackage interface and uses the registration attributes defined in the framework to
    ///     register itself and its components with the shell.
    /// </summary>
    // This attribute tells the PkgDef creation utility (CreatePkgDef.exe) that this class is
    // a package.
    [PackageRegistration(UseManagedResourcesOnly = true)]
    // This attribute is used to register the information needed to show this package
    // in the Help/About dialog of Visual Studio.
    [InstalledProductRegistration("#110", "#112", Version, IconResourceID = 400)]
    // This attribute is needed to let the shell know that this package exposes some menus.
    //[ProvideMenuResource("Menus.ctmenu", 1)]
    // This attribute registers a tool window exposed by this package.
    //[ProvideToolWindow(typeof (MyToolWindow))]
    [Guid(GuidList.guidXenko_VisualStudio_PackagePkgString)]
    // Xenko Shader LanguageService
    [ProvideService(typeof(NShaderLanguageService), ServiceName = "Xenko Shader Language Service")]
    [ProvideLanguageServiceAttribute(typeof(NShaderLanguageService),
                             "Xenko Shader Language",
                             0,
                             EnableCommenting = true,
                             EnableFormatSelection = true,
                             EnableLineNumbers = true,
                             DefaultToInsertSpaces = true,
                             CodeSense = true
                             )]
    [ProvideLanguageExtensionAttribute(typeof(NShaderLanguageService), NShaderSupportedExtensions.Xenko_Shader)]
    [ProvideLanguageExtensionAttribute(typeof(NShaderLanguageService), NShaderSupportedExtensions.Xenko_Effect)]
    // Xenko C# Shader Key Generator
    [CodeGeneratorRegistration(typeof(ShaderKeyFileGenerator), ShaderKeyFileGenerator.InternalName, GuidList.vsContextGuidVCSProject, GeneratorRegKeyName = ".xksl")]
    [CodeGeneratorRegistration(typeof(ShaderKeyFileGenerator), ShaderKeyFileGenerator.InternalName, GuidList.vsContextGuidVCSProject, GeneratorRegKeyName = ".xkfx")]
    [CodeGeneratorRegistration(typeof(ShaderKeyFileGenerator), ShaderKeyFileGenerator.DisplayName, GuidList.vsContextGuidVCSProject, GeneratorRegKeyName = ShaderKeyFileGenerator.InternalName, GeneratesDesignTimeSource = true, GeneratesSharedDesignTimeSource = true)]
    // Temporarily force load for easier debugging
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string)]
    public sealed class XenkoPackage : Package, IOleComponent
    {
        public const string Version = "1.4";

        private readonly Dictionary<EnvDTE.Project, string> previousProjectPlatforms = new Dictionary<EnvDTE.Project, string>();
        private EnvDTE.Project currentStartupProject;
        private bool configurationLock;

        private DTE2 dte2;
        private AppDomain buildMonitorDomain;
        private BuildLogPipeGenerator buildLogPipeGenerator;
        private SolutionEventsListener solutionEventsListener;
        private ErrorListProvider errorListProvider;
        private uint m_componentID;

        /// <summary>
        ///     Default constructor of the package.
        ///     Inside this method you can place any initialization code that does not require
        ///     any Visual Studio service because at this point the package object is created but
        ///     not sited yet inside Visual Studio environment. The place to do all the other
        ///     initialization is the Initialize method.
        /// </summary>
        public XenkoPackage()
        {
            Debug.WriteLine(string.Format(CultureInfo.CurrentCulture, "Entering constructor for: {0}", ToString()));
        }

        /// <summary>
        ///     This function is called when the user clicks the menu item that shows the
        ///     tool window. See the Initialize method to see how the menu item is associated to
        ///     this function using the OleMenuCommandService service and the MenuCommand class.
        /// </summary>
        private void ShowToolWindow(object sender, EventArgs e)
        {
            // Get the instance number 0 of this tool window. This window is single instance so this instance
            // is actually the only one.
            // The last flag is set to true so that if the tool window does not exists it will be created.
            //ToolWindowPane window = FindToolWindow(typeof (MyToolWindow), 0, true);
            //if ((null == window) || (null == window.Frame))
            //{
            //    throw new NotSupportedException(Resources.CanNotCreateWindow);
            //}
            //var windowFrame = (IVsWindowFrame) window.Frame;
            //ErrorHandler.ThrowOnFailure(windowFrame.Show());
        }

        private void MenuItemCallback(object sender, EventArgs e)
        {
            Environment.SetEnvironmentVariable("Test1", "Test2");
        }

        #region Package Members

        /// <summary>
        ///     Initialization of the package; this method is called right after the package is sited, so this is the place
        ///     where you can put all the initialization code that rely on services provided by VisualStudio.
        /// </summary>
        protected override void Initialize()
        {
            Debug.WriteLine(string.Format(CultureInfo.CurrentCulture, "Entering Initialize() of: {0}", ToString()));
            base.Initialize();

            IDEBuildLogger.UserRegistryRoot = UserRegistryRoot;

            solutionEventsListener = new SolutionEventsListener(this);
            solutionEventsListener.AfterSolutionBackgroundLoadComplete += solutionEventsListener_AfterSolutionBackgroundLoadComplete;
            solutionEventsListener.AfterActiveConfigurationChange += SolutionEventsListener_AfterActiveConfigurationChange;
            solutionEventsListener.StartupProjectChanged += SolutionEventsListener_OnStartupProjectChanged;

            dte2 = GetGlobalService(typeof(SDTE)) as DTE2;

            // Register the C# language service
            var serviceContainer = this as IServiceContainer;
            errorListProvider = new ErrorListProvider(this)
            {
                ProviderGuid = new Guid("ad1083c5-32ad-403d-af3d-32fee7abbdf1"),
                ProviderName = "Xenko Shading Language"
            };
            var langService = new NShaderLanguageService(errorListProvider);
            langService.SetSite(this);
            langService.InitializeColors(); // Make sure to initialize colors before registering!
            serviceContainer.AddService(typeof(NShaderLanguageService), langService, true);

            // Add our command handlers for menu (commands must exist in the .vsct file)
            var mcs = GetService(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (null != mcs)
            {
                XenkoCommands.ServiceProvider = this;
                XenkoCommands.RegisterCommands(mcs);
            }

            // Register a timer to call our language service during
            // idle periods.
            var mgr = GetService(typeof(SOleComponentManager))
                                       as IOleComponentManager;
            if (m_componentID == 0 && mgr != null)
            {
                OLECRINFO[] crinfo = new OLECRINFO[1];
                crinfo[0].cbSize = (uint)Marshal.SizeOf(typeof(OLECRINFO));
                crinfo[0].grfcrf = (uint)_OLECRF.olecrfNeedIdleTime |
                                              (uint)_OLECRF.olecrfNeedPeriodicIdleTime;
                crinfo[0].grfcadvf = (uint)_OLECADVF.olecadvfModal |
                                              (uint)_OLECADVF.olecadvfRedrawOff |
                                              (uint)_OLECADVF.olecadvfWarningsOff;
                crinfo[0].uIdleTimeInterval = 1000;
                int hr = mgr.FRegisterComponent(this, crinfo, out m_componentID);
            }
        }

        public static bool IsProjectExecutable(EnvDTE.Project project)
        {
            var buildProjects = ProjectCollection.GlobalProjectCollection.GetLoadedProjects(project.FileName);
            return buildProjects.Any(x => x.GetPropertyValue("SiliconStudioProjectType") == "Executable");
        }

        public static string GetProjectPlatform(EnvDTE.Project project)
        {
            var buildProjects = ProjectCollection.GlobalProjectCollection.GetLoadedProjects(project.FileName);
            return buildProjects.Select(x => x.GetPropertyValue("SiliconStudioPlatform")).FirstOrDefault(x => !string.IsNullOrEmpty(x));
        }

        private void SolutionEventsListener_OnStartupProjectChanged(IVsHierarchy hierarchy)
        {
            if (configurationLock || hierarchy == null)
                return;

            currentStartupProject = VsHelper.ToDteProject(hierarchy);

            UpdateConfigurationFromStartupProject();
        }

        private void SolutionEventsListener_AfterActiveConfigurationChange(IVsCfg oldConfiguration, IVsCfg newConfiguration)
        {
            if (configurationLock || newConfiguration == null)
                return;

            // TODO: Intercept Xamarin more gracefully. It tries to to set platform to "Any Cpu" for android and "iPhone"/"iPhoneSimulator" for iOS.
            foreach (System.Diagnostics.StackFrame stackFrame in new StackTrace().GetFrames())
            {
                var method = stackFrame.GetMethod();
                if (method.DeclaringType.FullName == "Xamarin.VisualStudio.TastyFlavoredProject" &&
                    method.Name == "OnAfterSetStartupProjectCommandExecuted")
                {
                    UpdateConfigurationFromStartupProject();
                    return;
                }
            }

            UpdateStartupProjectFromConfiguration();
        }

        private void UpdateConfigurationFromStartupProject()
        {
            if (currentStartupProject == null)
                return;

            var projectPlatform = GetProjectPlatform(currentStartupProject);
            var dte = (DTE)GetService(typeof(DTE));
            var activeConfiguration = dte.Solution.SolutionBuild.ActiveConfiguration;

            string startupPlatform;
            var hasPreviousPlatform = previousProjectPlatforms.TryGetValue(currentStartupProject, out startupPlatform);

            SolutionConfiguration2 newConfiguration = null;

            bool foundPreferredPlatform = false;
            foreach (SolutionConfiguration2 configuration in dte.Solution.SolutionBuild.SolutionConfigurations)
            {
                if (foundPreferredPlatform)
                    break;

                if (configuration.Name != activeConfiguration.Name)
                    continue;

                if ((projectPlatform == null) || !configuration.PlatformName.StartsWith(projectPlatform))
                    continue;

                foreach (SolutionContext context in configuration.SolutionContexts)
                {
                    if (!context.ShouldBuild || context.ProjectName != currentStartupProject.UniqueName)
                        continue;

                    if (hasPreviousPlatform && context.PlatformName != startupPlatform)
                        continue;

                    newConfiguration = configuration;

                    if (IsPreferredPlatform(projectPlatform, context.PlatformName))
                    {
                        foundPreferredPlatform = true;
                        break;
                    }
                }
            }

            if (newConfiguration != null && newConfiguration != activeConfiguration)
            {
                try
                {
                    configurationLock = true;
                    newConfiguration.Activate();
                }
                finally
                {
                    configurationLock = false;
                }
            }
        }

        private static bool IsPreferredPlatform(string projectPlatform, string platformName)
        {
            // Prefer non-ARM
            return (platformName != "ARM");
        }

        private void UpdateStartupProjectFromConfiguration()
        { 
            var solution = (IVsSolution)GetGlobalService(typeof(IVsSolution));
            var buildManager = (IVsSolutionBuildManager)GetGlobalService(typeof(IVsSolutionBuildManager));
            var dte = (DTE)GetService(typeof(DTE));

            foreach (SolutionContext context in dte.Solution.SolutionBuild.ActiveConfiguration.SolutionContexts)
            {
                if (!context.ShouldBuild)
                    continue;

                foreach (var project in VsHelper.GetDteProjectsInSolution(solution))
                {
                    if (context.ProjectName != project.UniqueName || !IsProjectExecutable(project))
                        continue;

                    var startupProjects = (object[])dte.Solution.SolutionBuild.StartupProjects;
                    if (!startupProjects.Cast<string>().Contains(project.UniqueName))
                    {
                        try
                        {
                            configurationLock = true;
                            buildManager.set_StartupProject(VsHelper.ToHierarchy(project));
                        }
                        finally
                        {
                            configurationLock = false;
                        }
                    }

                    previousProjectPlatforms[project] = context.PlatformName;
                    return;
                }
            }
        }

        private void solutionEventsListener_AfterSolutionBackgroundLoadComplete()
        {
            InitializeCommandProxy();

            var solution = (IVsSolution)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(IVsSolution));
            var updatedProjects = new List<string>();

            foreach (var dteProject in VsHelper.GetDteProjectsInSolution(solution))
            {
                var buildProjects = ProjectCollection.GlobalProjectCollection.GetLoadedProjects(dteProject.FileName);
                foreach (var buildProject in buildProjects)
                {
                    var packageVersion = buildProject.GetPropertyValue("SiliconStudioPackageXenkoVersion");
                    var currentPackageVersion = buildProject.GetPropertyValue("SiliconStudioPackageXenkoVersionLast");
                    if (!string.IsNullOrEmpty(packageVersion) && packageVersion != currentPackageVersion)
                    {
                        var buildPropertyStorage = VsHelper.ToHierarchy(dteProject) as IVsBuildPropertyStorage;
                        if (buildPropertyStorage != null)
                        {
                            buildPropertyStorage.SetPropertyValue("SiliconStudioPackageXenkoVersionLast", string.Empty, (uint)_PersistStorageType.PST_USER_FILE, packageVersion);

                            // Only "touch" file if there was a version before (we don't want to trigger this on newly created projects)
                            if (!string.IsNullOrEmpty(currentPackageVersion))
                                updatedProjects.Add(buildProject.FullPath);
                        }
                    }
                }
            }

            if (updatedProjects.Count > 0)
            {
                var messageBoxResult = VsShellUtilities.ShowMessageBox(this,
                    "Xenko needs to update IntelliSense cache for some projects.\nThis will resave the .csproj and Visual Studio will offer to reload them.\n\nProceed?",
                    "Xenko IntelliSense cache",
                    OLEMSGICON.OLEMSGICON_QUERY,
                    OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

                if (messageBoxResult == 6) // Yes
                {
                    // Touch files so that VS reload them
                    foreach (var updatedProject in updatedProjects)
                    {
                        File.SetLastWriteTimeUtc(updatedProject, DateTime.UtcNow);
                    }
                }
            }
        }

        private void InitializeCommandProxy()
        {
            // Initialize the command proxy from the current solution's package
            var dte = (DTE)GetService(typeof(DTE));
            var solutionPath = dte.Solution.FullName;
            XenkoCommandsProxy.InitialzeFromSolution(solutionPath);

            // Get General Output pane (for error logging)
            var generalOutputPane = GetGeneralOutputPane();

            // If a package is associated with the solution, check if the correct version was found
            var xenkoPackageInfo = XenkoCommandsProxy.CurrentPackageInfo;
            if (xenkoPackageInfo.ExpectedVersion != null && xenkoPackageInfo.ExpectedVersion != xenkoPackageInfo.LoadedVersion)
            {
                if (xenkoPackageInfo.ExpectedVersion < XenkoCommandsProxy.MinimumVersion)
                {
                    // The package version is deprecated
                    generalOutputPane.OutputStringThreadSafe($"Could not initialize Xenko extension for package with version {xenkoPackageInfo.ExpectedVersion}. Versions earlier than {XenkoCommandsProxy.MinimumVersion} are not supported. Loading latest version {xenkoPackageInfo.LoadedVersion} instead.\r\n");
                    generalOutputPane.Activate();
                }
                else if (xenkoPackageInfo.LoadedVersion == null)
                {
                    // No version found
                    generalOutputPane.OutputStringThreadSafe("Could not find Xenko SDK directory.");
                    generalOutputPane.Activate();

                    // Don't try to create any services
                    return;
                }
                else
                {
                    // The package version was not found
                    generalOutputPane.OutputStringThreadSafe($"Could not find SDK directory for Xenko version {xenkoPackageInfo.ExpectedVersion}. Loading latest version {xenkoPackageInfo.LoadedVersion} instead.\r\n");
                    generalOutputPane.Activate();
                }
            }

            // Initialize the build monitor, that will display BuildEngine results in the Build Output pane.
            // Seems like VS2015 display <Exec> output directly without waiting end of execution, so no need for all this anymore!
            // TODO: Need to find a better way to detect VS version?
            int visualStudioVersion;
            if (!int.TryParse(dte2.Version.Split('.')[0], out visualStudioVersion))
                visualStudioVersion = 12;

            if (visualStudioVersion < 14)
            {
                buildLogPipeGenerator = new BuildLogPipeGenerator(this);

                try
                {
                    // Start PackageBuildMonitorRemote in a separate app domain
                    if (buildMonitorDomain != null)
                        AppDomain.Unload(buildMonitorDomain);

                    buildMonitorDomain = XenkoCommandsProxy.CreateXenkoDomain();
                    XenkoCommandsProxy.InitialzeFromSolution(solutionPath, buildMonitorDomain);
                    var remoteCommands = XenkoCommandsProxy.CreateProxy(buildMonitorDomain);
                    remoteCommands.StartRemoteBuildLogServer(new BuildMonitorCallback(), buildLogPipeGenerator.LogPipeUrl);
                }
                catch (Exception e)
                {
                    generalOutputPane.OutputStringThreadSafe($"Error loading Xenko SDK: {e}\r\n");
                    generalOutputPane.Activate();

                    // Unload domain right away
                    AppDomain.Unload(buildMonitorDomain);
                    buildMonitorDomain = null;
                }
            }

            // Preinitialize the parser in a separate thread
            var thread = new System.Threading.Thread(
                () =>
                {
                    try
                    {
                        XenkoCommandsProxy.GetProxy();
                    }
                    catch (Exception ex)
                    {
                        generalOutputPane.OutputStringThreadSafe($"Error Initializing Xenko Language Service: {ex.InnerException ?? ex}\r\n");
                        generalOutputPane.Activate();
                        errorListProvider.Tasks.Add(new ErrorTask(ex.InnerException ?? ex));
                    }
                });
            thread.Start();
        }

        protected override void Dispose(bool disposing)
        {
            // Unload build monitor pipe domain
            if (buildMonitorDomain != null)
                AppDomain.Unload(buildMonitorDomain);

            if (m_componentID != 0)
            {
                IOleComponentManager mgr = GetService(typeof(SOleComponentManager))
                                           as IOleComponentManager;
                if (mgr != null)
                {
                    int hr = mgr.FRevokeComponent(m_componentID);
                }
                m_componentID = 0;
            }

            base.Dispose(disposing);
        }

        private IVsOutputWindowPane GetGeneralOutputPane()
        {
            var outputWindow = (IVsOutputWindow)Package.GetGlobalService(typeof(SVsOutputWindow));

            // Get Output pane
            IVsOutputWindowPane pane;
            Guid generalPaneGuid = VSConstants.GUID_OutWindowGeneralPane;
            outputWindow.CreatePane(ref generalPaneGuid, "General", 1, 0);
            outputWindow.GetPane(ref generalPaneGuid, out pane);
            return pane;
        }

        #endregion


        #region IOleComponent Members

        public int FDoIdle(uint grfidlef)
        {
            bool bPeriodic = (grfidlef & (uint)_OLEIDLEF.oleidlefPeriodic) != 0;
            // Use typeof(TestLanguageService) because we need to
            // reference the GUID for our language service.
            var service = GetService(typeof(NShaderLanguageService)) as LanguageService;
            if (service != null)
            {
                service.OnIdle(bPeriodic);
            }
            return 0;
        }

        public int FContinueMessageLoop(uint uReason,
                                        IntPtr pvLoopData,
                                        MSG[] pMsgPeeked)
        {
            return 1;
        }

        public int FPreTranslateMessage(MSG[] pMsg)
        {
            return 0;
        }

        public int FQueryTerminate(int fPromptUser)
        {
            return 1;
        }

        public int FReserved1(uint dwReserved,
                              uint message,
                              IntPtr wParam,
                              IntPtr lParam)
        {
            return 1;
        }

        public IntPtr HwndGetWindow(uint dwWhich, uint dwReserved)
        {
            return IntPtr.Zero;
        }

        public void OnActivationChange(IOleComponent pic,
                                       int fSameComponent,
                                       OLECRINFO[] pcrinfo,
                                       int fHostIsActivating,
                                       OLECHOSTINFO[] pchostinfo,
                                       uint dwReserved)
        {
        }

        public void OnAppActivate(int fActive, uint dwOtherThreadID)
        {
        }

        public void OnEnterState(uint uStateID, int fEnter)
        {
        }

        public void OnLoseActivation()
        {
        }

        public void Terminate()
        {
        }

        #endregion

    }
}
