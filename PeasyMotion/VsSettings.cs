﻿// Based on EditorHostFactory from VsVim
// And VsSettings from VsTeXCommentsExtension

/* VsSettings 
The MIT License (MIT)

Copyright (c) 2016 Hubert Kindermann

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

/* VsVim
Copyright 2012 Jared Parsons

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Microsoft.VisualStudio.Threading;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.Win32;
using System;
using System.Drawing;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Media;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;

using System.Threading;
using System.Windows.Threading;
using wpf = System.Windows.Media;

namespace PeasyMotion
{
    public sealed partial class EditorHostFactory
    {
        /// <summary>
        /// Beginning in 15.0 the editor took a dependency on JoinableTaskContext.  Need to provide that 
        /// export here. 
        /// </summary>
        private sealed class JoinableTaskContextExportProvider : ExportProvider
        {
            internal static string TypeFullName => typeof(JoinableTaskContext).FullName;
            private readonly Export _export;
            private readonly JoinableTaskContext _context;

            internal JoinableTaskContextExportProvider()
            {
                _export = new Export(TypeFullName, GetValue);
                _context =  Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskContext;
            }

            protected override IEnumerable<Export> GetExportsCore(ImportDefinition definition, AtomicComposition atomicComposition)
            {
                if (definition.ContractName == TypeFullName)
                { 
                    yield return _export;
                }
            }

            private object GetValue() => _context;
        }
    }

    public sealed class VsSettings : IDisposable, INotifyPropertyChanged
    {
        private static readonly SolidColorBrush DefaultForegroundBrush = new SolidColorBrush(wpf.Colors.Sienna);
        private static readonly SolidColorBrush DefaultBackgroundBrush = new SolidColorBrush(wpf.Colors.GreenYellow);
        private static readonly Dictionary<IWpfTextView, VsSettings> Instances = new Dictionary<IWpfTextView, VsSettings>();

        public static bool IsInitialized { get; private set; }
        private static IEditorFormatMapService editorFormatMapService;
        private static IServiceProvider serviceProvider;

        private readonly IWpfTextView textView;
        private readonly IEditorFormatMap editorFormatMap;

        private SolidColorBrush highContrastSelectionFg;
        public SolidColorBrush HighContrastSelectionFg
        {
            get { return highContrastSelectionFg; }
            private set { 
                bool notify = HighContrastSelectionFg == null || value == null || value.Color != HighContrastSelectionFg.Color;
                highContrastSelectionFg = value; 
                highContrastSelectionFg.Freeze(); 
                if (notify) OnPropertyChanged(nameof(HighContrastSelectionFg)); 
            }
        }
        private SolidColorBrush highContrastSelectionBg;
        public SolidColorBrush HighContrastSelectionBg
        {
            get { return highContrastSelectionBg; }
            private set { 
                bool notify = HighContrastSelectionBg == null || value == null || value.Color != HighContrastSelectionBg.Color;
                highContrastSelectionBg = value; 
                highContrastSelectionBg.Freeze();
                if (notify) OnPropertyChanged(nameof(HighContrastSelectionBg)); 
            }
        }

        private SolidColorBrush jumpLabelFg;
        public SolidColorBrush JumpLabelForegroundColor
        {
            get { return jumpLabelFg; }
            private set { 
                bool notify = JumpLabelForegroundColor == null || value == null || value.Color != JumpLabelForegroundColor.Color;
                jumpLabelFg = value; 
                jumpLabelFg.Freeze();
                Trace.WriteLine($"VsSettings.Property Set FG={JumpLabelForegroundColor.Color} notify={notify}");
                if (notify) OnPropertyChanged(nameof(JumpLabelForegroundColor)); 
            }
        }
        private SolidColorBrush jumpLabelBg;
        public SolidColorBrush JumpLabelBackgroundColor
        {
            get { return jumpLabelBg; }
            private set { 
                bool notify = JumpLabelBackgroundColor == null || value == null || value.Color != JumpLabelBackgroundColor.Color;
                jumpLabelBg = value;
                jumpLabelBg.Freeze();
                Trace.WriteLine($"VsSettings.Property Set BG={JumpLabelBackgroundColor.Color} notify={notify}");
                if (notify) OnPropertyChanged(nameof(JumpLabelBackgroundColor)); 
            }
        }
            

        public event PropertyChangedEventHandler PropertyChanged;

        // https://stackoverflow.com/questions/10283206/setting-getting-the-class-properties-by-string-name
        public object this[string propertyName] 
        {
            get{
                // probably faster without reflection:
                // like:  return Properties.Settings.Default.PropertyValues[propertyName] 
                // instead of the following
                Type myType = typeof(VsSettings);                   
                PropertyInfo myPropInfo = myType.GetProperty(propertyName);
                return myPropInfo.GetValue(this, null);
            } 
            set{
                Type myType = typeof(VsSettings);                   
                PropertyInfo myPropInfo = myType.GetProperty(propertyName);
                myPropInfo.SetValue(this, value, null);
            }
        }

        public static void NotiifyInstancesFmtPropertyChanged(string propertyName, System.Windows.Media.Color value) 
        {
            Trace.WriteLine($"GeneralOptions.SetColor -> VsSettings.NotiifyInstancesFmtPropertyChanged color={value}");
            lock (Instances)
            {
                var sb = new SolidColorBrush(value); 
                sb.Freeze();
                foreach (var i in Instances) { 
                    i.Value[propertyName] = sb; 
                }
            }
        }

        public static VsSettings GetOrCreate(IWpfTextView textView)
        {
            lock (Instances)
            {
                if (!Instances.TryGetValue(textView, out VsSettings settings))
                {
                    settings = new VsSettings(textView);
                    Instances.Add(textView, settings);
                }
                return settings;
            }
        }

        public static void Initialize(IServiceProvider serviceProviderx, IEditorFormatMapService editorFormatMapService)
        {
            if (IsInitialized)
                throw new InvalidOperationException($"{nameof(VsSettings)} class is already initialized.");

            IsInitialized = true;

            DefaultForegroundBrush.Freeze();
            DefaultBackgroundBrush.Freeze();

            VsSettings.editorFormatMapService = editorFormatMapService;
            VsSettings.serviceProvider = serviceProviderx;
            GeneralOptions.Instance.LoadColors(VsSettings.serviceProvider);
        }

        public VsSettings(IWpfTextView textView)
        {
            Debug.Assert(IsInitialized);

            this.textView = textView;
            editorFormatMap = editorFormatMapService.GetEditorFormatMap(textView);
            ReloadColors();

            editorFormatMap.FormatMappingChanged += OnFormatItemsChanged;
            textView.BackgroundBrushChanged += OnBackgroundBrushChanged;
        }

        private void ReloadColors()
        {
            //GeneralOptions.Instance.LoadColors(VsSettings.serviceProvider);
            HighContrastSelectionFg = GetBrush(editorFormatMap, "Selected Text in High Contrast", BrushType.Foreground, textView);
            HighContrastSelectionBg = GetBrush(editorFormatMap, "Selected Text in High Contrast", BrushType.Background, textView);

            Trace.WriteLine("VsSettings.ReloadColors settings FG & BG brushes");
            JumpLabelForegroundColor = GetBrush(editorFormatMap, PeasyMotionJumplabelFormatDef.FMT_NAME, BrushType.Foreground, textView);
            JumpLabelBackgroundColor = GetBrush(editorFormatMap, PeasyMotionJumplabelFormatDef.FMT_NAME, BrushType.Background, textView);
            Trace.WriteLine($"JUMP LABEL FG={JumpLabelForegroundColor.Color} BG={JumpLabelBackgroundColor.Color}");

           //HighContrastSelectionFg = GetBrush(editorFormatMap, "HTML Operator", BrushType.Foreground, textView);
           //HighContrastSelectionBg = GetBrush(editorFormatMap, "HTML Operator", BrushType.Background, textView);
        }

        private void OnFormatItemsChanged(object sender, FormatItemsEventArgs args)
        {
            if (args.ChangedItems.Any(i => i == PeasyMotionJumplabelFormatDef.FMT_NAME))
            {
                ReloadColors();
                Trace.WriteLine("VsSettings.OnFormatItemsChanged, FG={JumpLabelForegroundColor.Color} BG={JumpLabelBackgroundColor}");
                Trace.WriteLine("VsSettings.OnFormatItemsChanged Setting GeneralOptions FG & BG");
                GeneralOptions.Instance.JumpLabelForegroundColor = GeneralOptions.toDrawingColor(this.JumpLabelForegroundColor.Color);
                GeneralOptions.Instance.JumpLabelBackgroundColor = GeneralOptions.toDrawingColor(this.JumpLabelBackgroundColor.Color);

               // editorFormatMap.FormatMappingChanged -= OnFormatItemsChanged;
                //Trace.WriteLine("VsSettings.OnFormatItemsChanged Saving GeneralOptions.SaveColors");
                //GeneralOptions.Instance.SaveColors(VsSettings.serviceProvider);
                //editorFormatMap.FormatMappingChanged += OnFormatItemsChanged;
            }
        }

        private void OnBackgroundBrushChanged(object sender, BackgroundBrushChangedEventArgs args)
        {
            ReloadColors();
        }
        private void OnForegroundBrushChanged(object sender, BackgroundBrushChangedEventArgs args)
        {
            ReloadColors();
        }

        private static SolidColorBrush GetBrush(IEditorFormatMap editorFormatMap, string propertyName, BrushType type, IWpfTextView textView)
        {
            var props = editorFormatMap.GetProperties(propertyName);
            var typeText = type.ToString();

            object value = null;
            if (props.Contains(typeText))
            {
                value = props[typeText];
            }
            else
            {
                typeText += "Color";
                if (props.Contains(typeText))
                {
                    value = props[typeText];
                    if (value is wpf.Color)
                    {
                        var color = (wpf.Color)value;
                        var cb = new SolidColorBrush(color);
                        cb.Freeze();
                        value = cb;
                    }
                }
                else
                {
                    //Background is often not found in editorFormatMap. Don't know why :(
                    if (type == BrushType.Background)
                    {
                        value = textView.Background;
                    }
                }
            }

            return (value as SolidColorBrush) ?? (type == BrushType.Background ? DefaultBackgroundBrush : DefaultForegroundBrush);
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void Dispose()
        {
            editorFormatMap.FormatMappingChanged -= OnFormatItemsChanged;
            textView.BackgroundBrushChanged -= OnBackgroundBrushChanged;
        }

        private enum BrushType
        {
            Foreground,
            Background
        }
    }

    public sealed partial class EditorHostFactory
    {
        internal static Version VisualStudioVersion => new Version(16, 0, 0, 0);
        internal static Version VisualStudioThreadingVersion => new Version(16, 0, 0, 0);

        internal static string[] CoreEditorComponents =
            new[]
            {
                "Microsoft.VisualStudio.Platform.VSEditor.dll",
                "Microsoft.VisualStudio.Text.Internal.dll",
                "Microsoft.VisualStudio.Text.Logic.dll",
                "Microsoft.VisualStudio.Text.UI.dll",
                "Microsoft.VisualStudio.Text.UI.Wpf.dll",
                "Microsoft.VisualStudio.Language.dll",
            };

        private readonly List<ComposablePartCatalog> _composablePartCatalogList = new List<ComposablePartCatalog>();
        private readonly List<ExportProvider> _exportProviderList = new List<ExportProvider>();

        public EditorHostFactory()
        {
            BuildCatalog();
        }

        public void Add(ComposablePartCatalog composablePartCatalog)
        {
            _composablePartCatalogList.Add(composablePartCatalog);
        }

        public void Add(ExportProvider exportProvider)
        {
            _exportProviderList.Add(exportProvider);
        }

        public CompositionContainer CreateCompositionContainer()
        {
            var catalog = new AggregateCatalog(_composablePartCatalogList.ToArray());
            return new CompositionContainer(catalog, _exportProviderList.ToArray());
        }

        private void BuildCatalog()
        {
            var editorAssemblyVersion = new Version(VisualStudioVersion.Major, 0, 0, 0);
            AppendEditorAssemblies(editorAssemblyVersion);
            AppendEditorAssembly("Microsoft.VisualStudio.Threading", VisualStudioThreadingVersion);
            _exportProviderList.Add(new JoinableTaskContextExportProvider());
            _composablePartCatalogList.Add(new AssemblyCatalog(typeof(EditorHostFactory).Assembly));
        }

        private void AppendEditorAssemblies(Version editorAssemblyVersion)
        {
            foreach (var name in CoreEditorComponents)
            {
                var simpleName = Path.GetFileNameWithoutExtension(name);
                AppendEditorAssembly(simpleName, editorAssemblyVersion);
            }
        }

        private void AppendEditorAssembly(string name, Version version)
        {
            var assembly = GetEditorAssembly(name, version);
            _composablePartCatalogList.Add(new AssemblyCatalog(assembly));
        }

        private static Assembly GetEditorAssembly(string assemblyName, Version version)
        {
            //var qualifiedName = $"{assemblyName}, Version={version}, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a, processorArchitecture=MSIL";
            var qualifiedName = $"{assemblyName}, Version={version}, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a";
            //var A = Assembly.GetExecutingAssembly().GetReferencedAssemblies();
            //foreach(var f in A) { Trace.WriteLine(f); }
            //foreach(var f in Assembly.GetExecutingAssembly().GetFiles()) { Trace.WriteLine(f.Name); }
            return Assembly.Load(qualifiedName);
        }
    }
}
/*
mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089
Microsoft.VisualStudio.OLE.Interop, Version=7.1.40304.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089
Microsoft.VisualStudio.TextManager.Interop, Version=7.1.40304.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.VisualStudio.Text.UI, Version=16.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
PresentationFramework, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35
System.Xaml, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089
System, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089
WindowsBase, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35
PresentationCore, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35
EnvDTE, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
System.Core, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089
System.ComponentModel.Composition, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089
Microsoft.VisualStudio.Shell.15.0, Version=16.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.VisualStudio.Editor, Version=16.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.VisualStudio.Text.Logic, Version=16.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.VisualStudio.Text.UI.Wpf, Version=16.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.VisualStudio.Shell.Framework, Version=16.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.VisualStudio.Threading, Version=16.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.VisualStudio.ComponentModelHost, Version=16.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.VisualStudio.Text.Data, Version=16.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.VisualStudio.CoreUtility, Version=16.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.VisualStudio.Shell.Interop.8.0, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.CSharp, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
System.Design, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.VisualStudio.Validation, Version=15.3.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a
*/