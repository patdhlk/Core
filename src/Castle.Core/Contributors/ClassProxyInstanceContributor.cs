// Copyright 2004-2010 Castle Project - http://www.castleproject.org/
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Castle.DynamicProxy.Contributors
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Reflection;
	using System.Runtime.Serialization;

	using Castle.DynamicProxy.Generators;
	using Castle.DynamicProxy.Generators.Emitters;
	using Castle.DynamicProxy.Generators.Emitters.CodeBuilders;
	using Castle.DynamicProxy.Generators.Emitters.SimpleAST;
	using Castle.DynamicProxy.Serialization;
	using Castle.DynamicProxy.Tokens;

	public class ClassProxyInstanceContributor : ProxyInstanceContributor
	{
		private readonly bool delegateToBaseGetObjectData;
		private readonly bool implementISerializable;
		private ConstructorInfo serializationConstructor;
		private readonly IList<FieldReference> serializedFields = new List<FieldReference>();

		public ClassProxyInstanceContributor(Type targetType, IList<MethodInfo> methodsToSkip, Type[] interfaces)
			: base(targetType, interfaces,ProxyTypeConstants.Class)
		{
#if !SILVERLIGHT
			if (targetType.IsSerializable)
			{
				implementISerializable = true;
				delegateToBaseGetObjectData = VerifyIfBaseImplementsGetObjectData(targetType, methodsToSkip);
			}
#endif
		}

		protected override Expression GetTargetReferenceExpression(ClassEmitter emitter)
		{
			return SelfReference.Self.ToExpression();
		}

		public override void Generate(ClassEmitter @class, ProxyGenerationOptions options)
		{
			var interceptors = @class.GetField("__interceptors");
#if !SILVERLIGHT
			if (implementISerializable)
			{
				ImplementGetObjectData(@class);
				Constructor(@class);
			}
#endif
			ImplementProxyTargetAccessor(@class, interceptors);
			foreach (var attribute in AttributeUtil.GetNonInheritableAttributes(targetType))
			{
				@class.DefineCustomAttribute(attribute);
			}
		}
#if !SILVERLIGHT

		protected override void AddAddValueInvocation(ArgumentReference serializationInfo, MethodEmitter getObjectData, FieldReference field)
		{
			serializedFields.Add(field);
			base.AddAddValueInvocation(serializationInfo, getObjectData, field);
		}

		protected override void CustomizeGetObjectData(AbstractCodeBuilder codebuilder, ArgumentReference serializationInfo, ArgumentReference streamingContext, ClassEmitter emitter)
		{
			codebuilder.AddStatement(new ExpressionStatement(
			                         	new MethodInvocationExpression(serializationInfo, SerializationInfoMethods.AddValue_Bool,
			                         	                               new ConstReference("__delegateToBase").ToExpression(),
			                         	                               new ConstReference(delegateToBaseGetObjectData ? 1 : 0).
			                         	                               	ToExpression())));

			if (delegateToBaseGetObjectData)
			{
				MethodInfo baseGetObjectData = targetType.GetMethod("GetObjectData",
				                                                    new[] {typeof (SerializationInfo), typeof (StreamingContext)});

				codebuilder.AddStatement(new ExpressionStatement(
				                         	new MethodInvocationExpression(baseGetObjectData,
				                         	                               serializationInfo.ToExpression(),
				                         	                               streamingContext.ToExpression())));
			}
			else
			{
				LocalReference members_ref = codebuilder.DeclareLocal(typeof (MemberInfo[]));
				LocalReference data_ref = codebuilder.DeclareLocal(typeof (object[]));

				codebuilder.AddStatement(new AssignStatement(members_ref,
				                                             new MethodInvocationExpression(null,
				                                                                            FormatterServicesMethods.
				                                                                            	GetSerializableMembers,
				                                                                            new TypeTokenExpression(targetType))));

				codebuilder.AddStatement(new AssignStatement(data_ref,
				                                             new MethodInvocationExpression(null,
				                                                                            FormatterServicesMethods.GetObjectData,
				                                                                            SelfReference.Self.ToExpression(),
				                                                                            members_ref.ToExpression())));

				codebuilder.AddStatement(new ExpressionStatement(
				                         	new MethodInvocationExpression(serializationInfo, SerializationInfoMethods.AddValue_Object,
				                         	                               new ConstReference("__data").ToExpression(),
				                         	                               data_ref.ToExpression())));
			}
		}

		private void Constructor(ClassEmitter emitter)
		{
#if !SILVERLIGHT
			if (!delegateToBaseGetObjectData) return;
			GenerateSerializationConstructor(emitter);
#endif
		}

		private void GenerateSerializationConstructor(ClassEmitter emitter)
		{
			var serializationInfo = new ArgumentReference(typeof(SerializationInfo));
			var streamingContext = new ArgumentReference(typeof(StreamingContext));

			var ctor = emitter.CreateConstructor(serializationInfo, streamingContext);

			ctor.CodeBuilder.AddStatement(
				new ConstructorInvocationStatement(serializationConstructor,
				                                   serializationInfo.ToExpression(),
				                                   streamingContext.ToExpression()));

			foreach (var field in serializedFields)
			{
				var getValue = new MethodInvocationExpression(serializationInfo,
				                                              SerializationInfoMethods.GetValue,
				                                              new ConstReference(field.Reference.Name).ToExpression(),
				                                              new TypeTokenExpression(field.Reference.FieldType));
				ctor.CodeBuilder.AddStatement(new AssignStatement(
				                              	field,
				                              	new ConvertExpression(field.Reference.FieldType,
				                              	                      typeof (object),
				                              	                      getValue)));
			}
			ctor.CodeBuilder.AddStatement(new ReturnStatement());
		}

		private bool VerifyIfBaseImplementsGetObjectData(Type baseType, IList<MethodInfo> methodsToSkip)
		{
			if (!typeof (ISerializable).IsAssignableFrom(baseType))
			{
				return false;
			}

			// If base type implements ISerializable, we have to make sure
			// the GetObjectData is marked as virtual
			var getObjectDataMethod = baseType.GetInterfaceMap(typeof (ISerializable)).TargetMethods[0];

			Debug.Assert(getObjectDataMethod.Name == "GetObjectData");
			if (getObjectDataMethod.IsPrivate) //explicit interface implementation
			{
				return false;
			}

			if (!getObjectDataMethod.IsVirtual || getObjectDataMethod.IsFinal)
			{
				var message = String.Format("The type {0} implements ISerializable, but GetObjectData is not marked as virtual. " +
				                            "Dynamic Proxy needs types implementing ISerializable to mark GetObjectData as virtual " +
				                            "to ensure correct serialization process.",
				                            baseType.FullName);
				throw new ArgumentException(message);
			}

			methodsToSkip.Add(getObjectDataMethod);

			serializationConstructor = baseType.GetConstructor(
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
				null,
				new[] { typeof(SerializationInfo), typeof(StreamingContext) },
				null);

			if (serializationConstructor == null)
			{
				String message = String.Format("The type {0} implements ISerializable, " +
				                               "but failed to provide a deserialization constructor",
				                               baseType.FullName);
				throw new ArgumentException(message);
			}

			return true;
		}
#endif
	}
}