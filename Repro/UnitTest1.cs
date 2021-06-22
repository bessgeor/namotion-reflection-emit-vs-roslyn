using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Namotion.Reflection;
using NUnit.Framework;

namespace Repro
{
    public class Tests
    {
        private static readonly AssemblyBuilder _assemblyBuilder =
            AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName(
                    $"DynAsm.Types.{Guid.NewGuid()}, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"),
                AssemblyBuilderAccess.RunAndCollect);


        private static readonly Type _refType = typeof(string);

        private static readonly string _propertyNameA = "A";
        private static readonly string _propertyNameB = "B";

        private static Type EmitTestType()
        {
            var moduleBuilder = _assemblyBuilder.DefineDynamicModule("DynModule");

            var compilerGeneratedAttrCtor =
                typeof(CompilerGeneratedAttribute).GetConstructor(Array.Empty<Type>());
            var debuggerBrowsableAttrCtor =
                typeof(DebuggerBrowsableAttribute).GetConstructor(new[] {typeof(DebuggerBrowsableState)});
            var nullableContextAttrCtor =
                typeof(Nullability)
                    .Assembly
                    .DefinedTypes
                    .First(x => x.Name == "NullableContextAttribute")
                    .DeclaredConstructors
                    .Single();
            var nullableAttrCtor =
                typeof(Nullability)
                    .Assembly
                    .DefinedTypes
                    .First(x => x.Name == "NullableAttribute")
                    .DeclaredConstructors
                    .Single(x => x.GetParameters().Select(x => x.ParameterType).SequenceEqual(new[] {typeof(byte)}));

            return EmitType();

            Type EmitType()
            {
                var typeName = "EmittedTestType";
                var typeBuilder = moduleBuilder!.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class);

                // emit NRT attributes
                typeBuilder.SetCustomAttribute(nullableContextAttrCtor!, new byte[] {1, 0, 2, 0, 0});
                typeBuilder.SetCustomAttribute(nullableAttrCtor!, new byte[] {1, 0, 0, 0, 0});

                typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

                generateProperty(_propertyNameA!);
                generateProperty(_propertyNameB!);

                return typeBuilder.CreateType()!; // finalize type building for type to be loadable

                void generateProperty(string propertyName)
                {
                    var type = _refType;

                    // Generate a property with backing field like it's done for auto-properties
                    // https://sharplab.io/#v2:C4LglgNgNAJiDUAfAxAOwK4QgQwEYQFMACA1PQgWAChqABAZiNoCYiBhIgb2qN6cdoBGAAwB+IgEEuAcwLAA3AGc58gL48+DJiPEAhGSuUL1VVUA for reference
                    var field = typeBuilder!.DefineField("<" + propertyName + ">k__BackingField", type,
                        FieldAttributes.Private);
                    field.SetCustomAttribute(compilerGeneratedAttrCtor!, new byte[] {1, 0, 0, 0});
                    field.SetCustomAttribute(debuggerBrowsableAttrCtor!, new byte[] {1, 0, 0, 0, 0, 0, 0, 0});
                    var propertyBuilder =
                        typeBuilder.DefineProperty(propertyName, PropertyAttributes.HasDefault, type, null);

                    var autoPropAttrs = MethodAttributes.Public | MethodAttributes.HideBySig |
                                        MethodAttributes.SpecialName;

                    // Generate getter method
                    var getter = typeBuilder.DefineMethod("get_" + propertyName, autoPropAttrs, type, Type.EmptyTypes);
                    var il = getter.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0); // Push "this" on the stack
                    il.Emit(OpCodes.Ldfld, field); // Load the field "_Name"
                    il.Emit(OpCodes.Ret); // Return
                    getter.SetCustomAttribute(compilerGeneratedAttrCtor!, new byte[] {1, 0, 0, 0});
                    propertyBuilder.SetGetMethod(getter);

                    // Generate setter method
                    var setter = typeBuilder.DefineMethod("set_" + propertyName, autoPropAttrs, null, new[] {type});
                    il = setter.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0); // Push "this" on the stack
                    il.Emit(OpCodes.Ldarg_1); // Push "value" on the stack
                    il.Emit(OpCodes.Stfld, field); // Set the backing field to "value"
                    il.Emit(OpCodes.Ret); // Return
                    setter.SetCustomAttribute(compilerGeneratedAttrCtor!, new byte[] {1, 0, 0, 0});
                    propertyBuilder.SetSetMethod(setter);
                }
            }
        }


        [Test]
        public void Test1()
        {
            var emit = EmitTestType();
            var code = typeof(RoslynProcessedType);
            AssertAttributes(emit.CustomAttributes, code.CustomAttributes);
            var propNames = new[] {_propertyNameA, _propertyNameB};
            foreach (var prop in propNames)
            {
                var emitProp = emit.GetProperty(prop, BindingFlags.Instance | BindingFlags.Public);
                var codeProp = code.GetProperty(prop, BindingFlags.Instance | BindingFlags.Public);
                AssertAttributes(emitProp!.CustomAttributes, codeProp!.CustomAttributes);
            }

            var codeFields = code
                .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
                .ToDictionary(x => x.Name, x => x.CustomAttributes);
            var emitFields = emit
                .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
                .ToDictionary(x => x.Name, x => x.CustomAttributes);
            foreach (var field in codeFields.Keys)
            {
                var emitField = emitFields[field];
                var codeField = codeFields[field];
                AssertAttributes(emitField, codeField);
            }

            var emitAsmAttr = emit.Assembly.CustomAttributes.Where(x => x.AttributeType.Name.Contains("Null"));
            var codeAsmAttr = code.Assembly.CustomAttributes.Where(x => x.AttributeType.Name.Contains("Null"));
            AssertAttributes(emitAsmAttr, codeAsmAttr);

            var emitMdlAttr = emit.Assembly.Modules.Single().CustomAttributes
                .Where(x => x.AttributeType.Name.Contains("Null"));
            var codeMdlAttr = code.Assembly.Modules.Single().CustomAttributes
                .Where(x => x.AttributeType.Name.Contains("Null"));
            AssertAttributes(emitMdlAttr, codeMdlAttr);

            foreach (var prop in propNames)
            {
                var emitProp = emit.GetProperty(prop, BindingFlags.Instance | BindingFlags.Public)
                    .ToContextualProperty();
                var codeProp = code.GetProperty(prop, BindingFlags.Instance | BindingFlags.Public)
                    .ToContextualProperty();
                Assert.That(emitProp.Nullability, Is.EqualTo(Nullability.Unknown));
                Assert.That(codeProp.Nullability, Is.EqualTo(Nullability.Nullable));
            }

            void AssertAttributes(IEnumerable<CustomAttributeData> a, IEnumerable<CustomAttributeData> b)
            {
                var x = a.Select(ToComparable);
                var y = b.Select(ToComparable);
                CollectionAssert.AreEqual(x, y);

                static string ToComparable(CustomAttributeData d) => d.ToString();
            }
        }

#nullable enable
        public class RoslynProcessedType
        {
            public string? A { get; set; }
            public string? B { get; set; }
        }
#nullable restore
    }
}