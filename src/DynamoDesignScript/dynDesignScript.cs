﻿//Copyright © Autodesk, Inc. 2012. All rights reserved.
//
//Licensed under the Apache License, Version 2.0 (the "License");
//you may not use this file except in compliance with the License.
//You may obtain a copy of the License at
//
//http://www.apache.org/licenses/LICENSE-2.0
//
//Unless required by applicable law or agreed to in writing, software
//distributed under the License is distributed on an "AS IS" BASIS,
//WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//See the License for the specific language governing permissions and
//limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Windows.Controls;

using Dynamo;
using Dynamo.Nodes;
using Dynamo.Connectors;
using Dynamo.FSchemeInterop;
using Dynamo.Utilities;
using Value = Dynamo.FScheme.Value;

using Microsoft.FSharp.Collections;

using IronPython;
using IronPython.Hosting;
using IronPython.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using System.Windows;
using System.Xml;
using Microsoft.FSharp.Core;

using Autodesk.Revit;
using Autodesk.Revit.DB;

using Dynamo.Revit;

using Autodesk.ASM;

// DS
using ProtoCore.DSASM.Mirror;
using ProtoCore.Lang;
using ProtoFFI;

namespace Dynamo.Nodes
{
    [NodeName("DesignScript Script")]
    [NodeCategory(BuiltinNodeCategories.SCRIPTING)]
    [NodeDescription("Runs an embedded DesignScript script")]
    //[Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Automatic)]
    public class dynDesignScript : dynRevitTransactionNodeWithOneOutput
    {
        private bool dirty = true;

        string script;

        ProtoCore.Core core;
        bool coreSet = false;

        private static bool asm_started = false;

        public dynDesignScript()
        {
            //add an edit window option to the 
            //main context window
            System.Windows.Controls.MenuItem editWindowItem = new System.Windows.Controls.MenuItem();
            editWindowItem.Header = "Edit...";
            editWindowItem.IsCheckable = false;
            NodeUI.MainContextMenu.Items.Add(editWindowItem);
            editWindowItem.Click += new RoutedEventHandler(editWindowItem_Click);

            InPortData.Add(new PortData("Bindings", "A list of <string, object> variable tuples", typeof(object)));
            OutPortData.Add(new PortData("OUT", "Result of the DesignScript script", typeof(object)));

            NodeUI.RegisterAllPorts();

            NodeUI.UpdateLayout();
        }

        internal void start_asm()
        {
            if (asm_started)
                return;

            //Autodesk.ASM.State.UseExternalASM();
            Autodesk.ASM.State.Start();
            Autodesk.ASM.State.StartViewer();

            asm_started = true;
        }

        public override bool RequiresRecalc
        {
            get
            {
                return true;
            }
            set { }
        }

        public override void SaveElement(XmlDocument xmlDoc, XmlElement dynEl)
        {
            XmlElement script = xmlDoc.CreateElement("Script");
            //script.InnerText = this.tb.Text;
            script.InnerText = this.script;
            dynEl.AppendChild(script);
        }

        public override void LoadElement(XmlNode elNode)
        {
            foreach (XmlNode subNode in elNode.ChildNodes)
            {
                if (subNode.Name == "Script")
                    //this.tb.Text = subNode.InnerText;
                    script = subNode.InnerText;
            }
        }

        public override Value Evaluate(FSharpList<Value> args)
        {
            if (coreSet)
            {
                core.Cleanup();
                Autodesk.ASM.State.ClearPersistedObjects();
                Autodesk.ASM.DynamoOutput.Reset();
            }
            else
            {
                start_asm();
                coreSet = true;
            }

            Dictionary<string, object> context = new Dictionary<string, object>();

            if (args[0].IsList)
            {
                FSharpList<Value> containers = Utils.SequenceToFSharpList(
                    ((Value.List)args[0]).Item);

                foreach (Value e in containers)
                {
                    if (!e.IsList)
                        continue;

                    FSharpList<Value> tuple = Utils.SequenceToFSharpList(
                        ((Value.List)e).Item);

                    string var_name = (string)((Value.String)tuple[0]).Item;
                    object var_value = null;

                    if (tuple[1].IsNumber)
                        var_value = (object)((Value.Number)tuple[1]).Item;
                    else if (tuple[1].IsContainer)
                        var_value = (object)((Value.Container)tuple[1]).Item;

                    if (var_value != null)
                        context.Add(var_name, var_value);
                }
            }

            core = new ProtoCore.Core(new ProtoCore.Options());
            core.Executives.Add(ProtoCore.Language.kAssociative, new ProtoAssociative.Executive(core));
            core.Executives.Add(ProtoCore.Language.kImperative, new ProtoImperative.Executive(core));

            ProtoScript.Runners.ProtoScriptTestRunner fsr = new ProtoScript.Runners.ProtoScriptTestRunner();

            ProtoFFI.DLLFFIHandler.Register(ProtoFFI.FFILanguage.CSharp, 
                new ProtoFFI.CSModuleHelper());

            ExecutionMirror mirror = fsr.Execute(script, core, context);

            FSharpList<Value> created_objects = FSharpList<Value>.Empty;

            List<object> output_objects = Autodesk.ASM.DynamoOutput.Objects();
            List<Autodesk.Revit.DB.ElementId> created_ids = new List<Autodesk.Revit.DB.ElementId>();

            string temp_dir = "C:\\Temp\\";

            foreach (object o in output_objects)
            {
                Autodesk.DesignScript.Geometry.Point p = o as Autodesk.DesignScript.Geometry.Point;

                if (p != null)
                {
                    Autodesk.Revit.DB.XYZ xyz = new Autodesk.Revit.DB.XYZ(p.X, p.Y, p.Z);
                    ReferencePoint elem = Dynamo.Utilities.dynRevitSettings.Doc.Document.FamilyCreate.NewReferencePoint(xyz);

                    created_ids.Add(elem.Id);

                    continue;
                }

                Autodesk.DesignScript.Geometry.Line l = o as Autodesk.DesignScript.Geometry.Line;

                if (l != null)
                {
                    Autodesk.Revit.DB.XYZ xyz_start = new Autodesk.Revit.DB.XYZ(
                        l.StartPoint.X, l.StartPoint.Y, l.StartPoint.Z);
                    Autodesk.Revit.DB.XYZ xyz_end = new Autodesk.Revit.DB.XYZ(
                        l.EndPoint.X, l.EndPoint.Y, l.EndPoint.Z);
                    Autodesk.Revit.DB.Line revit_line = 
                        Autodesk.Revit.DB.Line.CreateBound(xyz_start, xyz_end);

                    Autodesk.Revit.DB.DetailCurve line_elem = 
                        Dynamo.Utilities.dynRevitSettings.Doc.Document.FamilyCreate.NewDetailCurve(
                        Dynamo.Utilities.dynRevitSettings.Doc.ActiveView,
                        revit_line);

                    created_ids.Add(line_elem.Id);

                    continue;
                }

                Autodesk.DesignScript.Geometry.Geometry g = o as Autodesk.DesignScript.Geometry.Geometry;

                if (g == null)
                    continue;

                System.Guid guid = System.Guid.NewGuid();

                string temp_file_name = temp_dir + guid.ToString() + ".sat";

                g.ExportToSAT(temp_file_name);

                Autodesk.Revit.DB.SATImportOptions options = new
                    Autodesk.Revit.DB.SATImportOptions();

                Autodesk.Revit.DB.ElementId new_id =
                    Dynamo.Utilities.dynRevitSettings.Doc.Document.Import(
                    temp_file_name, options,
                    Dynamo.Utilities.dynRevitSettings.Doc.ActiveView);

                created_ids.Add(new_id);
            }

            //List<Autodesk.Revit.DB.Solid> solids = new List<Autodesk.Revit.DB.Solid>();
            //List<Autodesk.Revit.DB.Element> elements = new List<Autodesk.Revit.DB.Element>();

            //foreach (object o in output_objects)
            //{
                //Autodesk.DesignScript.Geometry.Geometry g = o as Autodesk.DesignScript.Geometry.Geometry;

                //if (g == null)
                //    continue;

                //Autodesk.DesignScript.Interfaces.IGeometryEntity entity =
                //Autodesk.DesignScript.Geometry.GeometryExtension.ToEntity<
                //    Autodesk.DesignScript.Geometry.Geometry,
                //    Autodesk.DesignScript.Interfaces.IGeometryEntity>(g);

                //Autodesk.ASM.DesignScriptEntity ds_entity = entity as Autodesk.ASM.DesignScriptEntity;

                //if (ds_entity == null)
                //    continue;

                //Autodesk.Revit.DB.Solid solid = Autodesk.Revit.DB.GeometryCreationUtilities.ConvertAsmBodyToGeometry(ds_entity.BodyPtr);
                //solids.Add(solid);

                //Autodesk.DesignScript.Geometry.Point p = o as Autodesk.DesignScript.Geometry.Point;

                //if (p == null)
                //    continue;

                //Autodesk.Revit.DB.XYZ xyz = Autodesk.Revit.DB.XYZ(p.X, p.Y, p.Z);

                ////FreeFormElement elem = Autodesk.Revit.DB.FreeFormElement.Create(
                ////    Dynamo.Utilities.dynRevitSettings.Doc.Document, solid);
                //ReferencePoint elem = Dynamo.Utilities.dynRevitSettings.Doc.Document.FamilyCreate.NewReferencePoint(xyz);


                //elements.Add(elem);
            //}

            //foreach (Autodesk.Revit.DB.Element revit_entity in elements)
            //{
            //    Value element = Value.NewContainer(revit_entity);
            //    created_objects = FSharpList<Value>.Cons(element, created_objects);
            //}

            foreach (Autodesk.Revit.DB.ElementId id in created_ids)
            {
                Value element = Value.NewContainer(id);
                created_objects = FSharpList<Value>.Cons(element, created_objects);
            }

            return Value.NewList(created_objects);
        }

        void editWindowItem_Click(object sender, RoutedEventArgs e)
        {
            dynEditWindow editWindow = new dynEditWindow();

            //set the text of the edit window to begin
            editWindow.editText.Text = script;

            if (editWindow.ShowDialog() != true)
            {
                return;
            }

            //set the value from the text in the box
            script = editWindow.editText.Text;

            this.dirty = true;
        }
    }

    [NodeName("DesignScript Script From String")]
    [NodeCategory(BuiltinNodeCategories.SCRIPTING)]
    [NodeDescription("Runs a DesignScript script from a string")]
    public class dynDesignScriptString : dynNodeWithOneOutput
    {
        public dynDesignScriptString()
        {
            InPortData.Add(new PortData("script", "Script to run", typeof(string)));
            InPortData.Add(new PortData("IN", "Input", typeof(object)));
            OutPortData.Add(new PortData("OUT", "Result of the python script", typeof(object)));

            NodeUI.RegisterAllPorts();
        }

        public override Value Evaluate(FSharpList<Value> args)
        {
            return Value.NewContainer(true);
        }
    }
}