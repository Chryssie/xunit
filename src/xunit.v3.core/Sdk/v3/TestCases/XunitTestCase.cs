using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Internal;
using Xunit.Sdk;

namespace Xunit.v3
{
	/// <summary>
	/// Default implementation of <see cref="IXunitTestCase"/> for xUnit v3 that supports test methods decorated with
	/// <see cref="FactAttribute"/>. Test methods decorated with derived attributes may use this as a base class
	/// to build from.
	/// </summary>
	[Serializable]
	[DebuggerDisplay(@"\{ class = {TestMethod.TestClass.Class.Name}, method = {TestMethod.Method.Name}, display = {TestCaseDisplayName}, skip = {SkipReason} \}")]
	public class XunitTestCase : TestMethodTestCase, IXunitTestCase
	{
		static readonly ConcurrentDictionary<string, IReadOnlyCollection<_IAttributeInfo>> assemblyTraitAttributeCache = new(StringComparer.OrdinalIgnoreCase);
		static readonly ConcurrentDictionary<string, IReadOnlyCollection<_IAttributeInfo>> typeTraitAttributeCache = new(StringComparer.OrdinalIgnoreCase);

		/// <inheritdoc/>
		protected XunitTestCase(
			SerializationInfo info,
			StreamingContext context) :
				base(info, context)
		{
			Timeout = info.GetValue<int>("Timeout");
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="XunitTestCase"/> class.
		/// </summary>
		/// <remarks>
		/// This constructor is intended to be used by test methods which are decorated directly with <see cref="FactAttribute"/>
		/// (and not any derived attribute). Developers creating custom attributes derived from <see cref="FactAttribute"/>
		/// should create their own test case class (derived from this) and use the protected constructor instead.
		/// </remarks>
		/// <param name="defaultMethodDisplay">Default method display to use (when not customized).</param>
		/// <param name="defaultMethodDisplayOptions">Default method display options to use (when not customized).</param>
		/// <param name="testMethod">The test method this test case belongs to.</param>
		/// <param name="skipReason">The optional reason for skipping the test; if not provided, will be read from the <see cref="FactAttribute"/>.</param>
		/// <param name="timeout">The optional timeout (in milliseconds); if not provided, will be read from the <see cref="FactAttribute"/>.</param>
		/// <param name="uniqueID">The optional unique ID for the test case; if not provided, will be calculated.</param>
		/// <param name="displayName">The optional display name for the test</param>
		public XunitTestCase(
			TestMethodDisplay defaultMethodDisplay,
			TestMethodDisplayOptions defaultMethodDisplayOptions,
			_ITestMethod testMethod,
			string? skipReason = null,
			int? timeout = null,
			string? uniqueID = null,
			string? displayName = null)
				: this(defaultMethodDisplay, defaultMethodDisplayOptions, testMethod, null, skipReason, null, timeout, uniqueID, displayName)
		{ }

		/// <summary>
		/// Initializes a new instance of the <see cref="XunitTestCase"/> class.
		/// </summary>
		/// <param name="defaultMethodDisplay">Default method display to use (when not customized).</param>
		/// <param name="defaultMethodDisplayOptions">Default method display options to use (when not customized).</param>
		/// <param name="testMethod">The test method this test case belongs to.</param>
		/// <param name="testMethodArguments">The arguments for the test method.</param>
		/// <param name="skipReason">The optional reason for skipping the test; if not provided, will be read from the <see cref="FactAttribute"/>.</param>
		/// <param name="traits">The optional traits list; if not provided, will be read from trait attributes.</param>
		/// <param name="timeout">The optional timeout (in milliseconds); if not provided, will be read from the <see cref="FactAttribute"/>.</param>
		/// <param name="uniqueID">The optional unique ID for the test case; if not provided, will be calculated.</param>
		/// <param name="displayName">The optional display name for the test</param>
		protected XunitTestCase(
			TestMethodDisplay defaultMethodDisplay,
			TestMethodDisplayOptions defaultMethodDisplayOptions,
			_ITestMethod testMethod,
			object?[]? testMethodArguments,
			string? skipReason,
			Dictionary<string, List<string>>? traits,
			int? timeout,
			string? uniqueID,
			string? displayName)
				: base(defaultMethodDisplay, defaultMethodDisplayOptions, testMethod, testMethodArguments, skipReason, traits, uniqueID, displayName)
		{
			var factAttribute = TestMethod.Method.GetCustomAttributes(typeof(FactAttribute)).First();
			var baseDisplayName = displayName ?? factAttribute.GetNamedArgument<string>("DisplayName") ?? BaseDisplayName;

			TestCaseDisplayName = TestMethod.Method.GetDisplayNameWithArguments(baseDisplayName, TestMethodArguments, MethodGenericTypes);
			SkipReason ??= factAttribute.GetNamedArgument<string>(nameof(FactAttribute.Skip));
			Timeout = timeout ?? factAttribute.GetNamedArgument<int>(nameof(FactAttribute.Timeout));

			foreach (var traitAttribute in GetTraitAttributesData(TestMethod))
			{
				var discovererAttribute = traitAttribute.GetCustomAttributes(typeof(TraitDiscovererAttribute)).FirstOrDefault();
				if (discovererAttribute != null)
				{
					var discoverer = ExtensibilityPointFactory.GetTraitDiscoverer(discovererAttribute);
					if (discoverer != null)
						foreach (var keyValuePair in discoverer.GetTraits(traitAttribute))
							Traits.Add(keyValuePair.Key, keyValuePair.Value);
				}
				else
					TestContext.Current?.SendDiagnosticMessage("Trait attribute on '{0}' did not have [TraitDiscoverer]", TestCaseDisplayName);
			}
		}

		/// <inheritdoc/>
		public int Timeout { get; protected set; }

		static IReadOnlyCollection<_IAttributeInfo> GetCachedTraitAttributes(_IAssemblyInfo assembly)
		{
			Guard.ArgumentNotNull(assembly);

			return assemblyTraitAttributeCache.GetOrAdd(assembly.Name, () => assembly.GetCustomAttributes(typeof(ITraitAttribute)));
		}

		static IReadOnlyCollection<_IAttributeInfo> GetCachedTraitAttributes(_ITypeInfo type)
		{
			Guard.ArgumentNotNull(type);

			return typeTraitAttributeCache.GetOrAdd(type.Name, () => type.GetCustomAttributes(typeof(ITraitAttribute)));
		}

		static IReadOnlyCollection<_IAttributeInfo> GetTraitAttributesData(_ITestMethod testMethod)
		{
			Guard.ArgumentNotNull(testMethod);

			return
				GetCachedTraitAttributes(testMethod.TestClass.Class.Assembly)
					.Concat(testMethod.Method.GetCustomAttributes(typeof(ITraitAttribute)))
					.Concat(GetCachedTraitAttributes(testMethod.TestClass.Class))
					.CastOrToReadOnlyCollection();
		}

		/// <inheritdoc/>
		public virtual ValueTask<RunSummary> RunAsync(
			IMessageBus messageBus,
			object?[] constructorArguments,
			ExceptionAggregator aggregator,
			CancellationTokenSource cancellationTokenSource)
		{
			Guard.ArgumentNotNull(messageBus);
			Guard.ArgumentNotNull(constructorArguments);
			Guard.ArgumentNotNull(cancellationTokenSource);

			return XunitTestCaseRunner.Instance.RunAsync(
				this,
				messageBus,
				aggregator,
				cancellationTokenSource,
				TestCaseDisplayName,
				SkipReason,
				constructorArguments,
				TestMethodArguments
			);
		}

		/// <inheritdoc/>
		public override void GetObjectData(
			SerializationInfo info,
			StreamingContext context)
		{
			base.GetObjectData(info, context);

			info.AddValue("Timeout", Timeout);
		}
	}
}
