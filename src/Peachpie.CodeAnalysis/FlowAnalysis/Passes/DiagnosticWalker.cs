﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Pchp.CodeAnalysis.Errors;
using Pchp.CodeAnalysis.Semantics;
using Pchp.CodeAnalysis.Semantics.Graph;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Pchp.CodeAnalysis.Symbols;
using Devsense.PHP.Syntax.Ast;
using Peachpie.CodeAnalysis.Utilities;
using Pchp.CodeAnalysis.Semantics.TypeRef;
using System.Text.RegularExpressions;
using Devsense.PHP.Syntax;

namespace Pchp.CodeAnalysis.FlowAnalysis.Passes
{
    internal partial class DiagnosticWalker<T> : GraphExplorer<T>
    {
        private static readonly Regex PrintfSpecsRegex = new Regex(@"%(?:(\d)+\$)?[+-]?(?:[ 0]|'.{1})?-?\d*(?:\.\d+)?[bcdeEufFgGosxX]");

        private readonly DiagnosticBag _diagnostics;
        private SourceRoutineSymbol _routine;

        private bool CallsParentCtor { get; set; }

        PhpCompilation DeclaringCompilation => _routine.DeclaringCompilation;

        TypeRefContext TypeCtx => _routine.TypeRefContext;

        #region Scope

        struct Scope
        {
            public enum Kind
            {
                Try, Catch, Finally,
            }

            public bool Contains(BoundBlock b) => b != null && b.Ordinal >= From && b.Ordinal < To;

            public bool IsTryCatch => ScopeKind == Kind.Try || ScopeKind == Kind.Catch || ScopeKind == Kind.Finally;

            public int From, To;
            public Kind ScopeKind;
        }

        List<Scope> _lazyScopes = null;

        /// <summary>
        /// Stores a scope (range) of blocks.
        /// </summary>
        void WithScope(Scope scope)
        {
            if (_lazyScopes == null) _lazyScopes = new List<Scope>();
            _lazyScopes.Add(scope);
        }

        bool IsInScope(Scope.Kind kind) => _lazyScopes != null && _lazyScopes.Any(s => s.ScopeKind == kind && s.Contains(_currentBlock));

        bool IsInTryCatchScope() => _lazyScopes != null && _lazyScopes.Any(s => s.IsTryCatch && s.Contains(_currentBlock));

        #endregion

        void CheckMissusedPrimitiveType(IBoundTypeRef tref)
        {
            if (tref.IsPrimitiveType)
            {
                // error: use of primitive type {0} is misused // primitive type does not make any sense in this context
                _diagnostics.Add(_routine, tref.PhpSyntax, ErrorCode.ERR_PrimitiveTypeNameMisused, tref);
            }
        }

        void Add(Devsense.PHP.Text.Span span, Devsense.PHP.Errors.ErrorInfo err, params string[] args)
        {
            _diagnostics.Add(DiagnosticBagExtensions.ParserDiagnostic(_routine, span, err, args));
        }

        void CannotInstantiate(IPhpOperation op, string kind, IBoundTypeRef t)
        {
            _diagnostics.Add(_routine, op.PhpSyntax, ErrorCode.ERR_CannotInstantiateType, kind, t.Type);
        }

        public static void Analyse(DiagnosticBag diagnostics, SourceRoutineSymbol routine)
        {
            //
            routine.GetDiagnostics(diagnostics);

            //
            if (routine.ControlFlowGraph != null)   // non-abstract method
            {
                new DiagnosticWalker<VoidStruct>(diagnostics, routine).VisitCFG(routine.ControlFlowGraph);
            }
        }

        private DiagnosticWalker(DiagnosticBag diagnostics, SourceRoutineSymbol routine)
        {
            _diagnostics = diagnostics;
            _routine = routine;
        }

        protected override void VisitCFGInternal(ControlFlowGraph x)
        {
            Debug.Assert(x == _routine.ControlFlowGraph);

            base.VisitCFGInternal(x);

            CheckParams();

            if (CallsParentCtor == false &&
                new Name(_routine.Name).IsConstructName &&
                HasBaseConstruct(_routine.ContainingType))
            {
                // Missing calling parent::__construct
                _diagnostics.Add(_routine, _routine.SyntaxSignature.Span.ToTextSpan(), ErrorCode.WRN_ParentCtorNotCalled, _routine.ContainingType.Name);
            }

            // analyse missing or redefined labels
            CheckLabels(x.Labels);

            // report unreachable blocks
            CheckUnreachableCode(x);
        }

        /// <summary>
        /// Checks the base class has implementation of `__construct` which should be called.
        /// </summary>
        /// <param name="type">Self.</param>
        /// <returns>Whether the base of <paramref name="type"/> has `__construct` method implementation.</returns>
        static bool HasBaseConstruct(NamedTypeSymbol type)
        {
            var btype = type?.BaseType;
            if (btype != null && btype.SpecialType != SpecialType.System_Object && btype.IsClassType() && !btype.IsAbstract)
            {
                var bconstruct = btype.ResolvePhpCtor();    // TODO: recursive: true // needs inf recursion prevention
                if (bconstruct != null && !bconstruct.IsAbstract)
                {
                    return true;
                }
            }

            return false;
        }

        private void CheckParams()
        {
            // Check the compatibility of type hints with PhpDoc, if both exist
            if (_routine.PHPDocBlock != null)
            {
                for (int i = 0; i < _routine.SourceParameters.Length; i++)
                {
                    var param = _routine.SourceParameters[i];

                    //// Consider only parameters passed by value, with both typehints and PHPDoc comments
                    //if (!param.Syntax.IsOut && !param.Syntax.PassedByRef
                    //    && param.Syntax.TypeHint != null
                    //    && param.PHPDocOpt != null && param.PHPDocOpt.TypeNamesArray.Length != 0)
                    //{
                    //    var tmask = PHPDoc.GetTypeMask(TypeCtx, param.PHPDocOpt.TypeNamesArray, _routine.GetNamingContext());
                    //    if (!tmask.IsVoid && !tmask.IsAnyType)
                    //    {
                    //        var hintType = param.Type;
                    //        var docType = DeclaringCompilation.GetTypeFromTypeRef(TypeCtx, tmask);
                    //        if (!docType.IsOfType(hintType))  // REVIEW: not correct, CLR type might result in PhpValue or anything else which is never "of type" specified in PHPDoc
                    //        {
                    //            // PHPDoc type is incompatible with type hint
                    //            _diagnostics.Add(_routine, param.Syntax, ErrorCode.WRN_ParamPhpDocTypeHintIncompatible,
                    //                param.PHPDocOpt.TypeNames, param.Name, param.Syntax.TypeHint);
                    //        }
                    //    }
                    //}
                }
            }
        }

        void CheckLabels(ImmutableArray<ControlFlowGraph.LabelBlockState> labels)
        {
            if (labels == null || labels.Length == 0)
            {
                return;
            }

            for (int i = 0; i < labels.Length; i++)
            {
                var flags = labels[i].Flags;
                if ((flags & ControlFlowGraph.LabelBlockFlags.Defined) == 0)
                {
                    Add(labels[i].LabelSpan, Devsense.PHP.Errors.Errors.UndefinedLabel, labels[i].Label);
                }
                if ((flags & ControlFlowGraph.LabelBlockFlags.Used) == 0)
                {
                    // Warning: label not used
                }
                if ((flags & ControlFlowGraph.LabelBlockFlags.Redefined) != 0)
                {
                    Add(labels[i].LabelSpan, Devsense.PHP.Errors.Errors.LabelRedeclared, labels[i].Label);
                }
            }
        }

        public override T VisitEval(BoundEvalEx x)
        {
            _diagnostics.Add(_routine, new TextSpan(x.PhpSyntax.Span.Start, 4)/*'eval'*/, ErrorCode.INF_EvalDiscouraged);

            return base.VisitEval(x);
        }

        public override T VisitArray(BoundArrayEx x)
        {
            if (x.Access.IsNone)
            {
                // The expression is not being read. Did you mean to assign it somewhere?
                _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_ExpressionNotRead);
            }

            // Check valid types and uniqueness of the keys
            HashSet<(string, long)> lazyKeyConstSet = null;             // Stores canonic string representations of the keys to check for duplicates
            for (int i = 0; i < x.Items.Length; i++)
            {
                var item = x.Items[i];
                if (item.Key == null)
                    continue;

                var keyTypeMask = item.Key.TypeRefMask;
                if (!keyTypeMask.IsAnyType && !keyTypeMask.IsRef)    // Disallowing 'mixed' for key type would have caused too many false positives
                {
                    var valid = !keyTypeMask.IsVoid;
                    foreach (var t in TypeCtx.GetTypes(keyTypeMask))
                    {
                        valid &= AnalysisFacts.IsValidKeyType(t);
                    }

                    if (!valid)
                    {
                        string keyTypeStr = TypeCtx.ToString(keyTypeMask);
                        _diagnostics.Add(_routine, item.Key.PhpSyntax, ErrorCode.WRN_InvalidArrayKeyType, keyTypeStr);
                    }
                }

                if (AnalysisFacts.TryGetCanonicKeyStringConstant(item.Key.ConstantValue, out var keyConst))
                {
                    if (lazyKeyConstSet == null)
                        lazyKeyConstSet = new HashSet<(string, long)>();

                    if (!lazyKeyConstSet.Add(keyConst))
                    {
                        // Duplicate array key: '{0}'
                        _diagnostics.Add(
                            _routine,
                            item.Key.PhpSyntax ?? item.Value.PhpSyntax,
                            ErrorCode.WRN_DuplicateArrayKey,
                            keyConst.Item1 ?? keyConst.Item2.ToString());
                    }
                }
            }

            return base.VisitArray(x);
        }

        internal override T VisitIndirectTypeRef(BoundIndirectTypeRef x)
        {
            return base.VisitIndirectTypeRef(x);
        }

        internal override T VisitTypeRef(BoundTypeRef typeRef)
        {
            CheckUndefinedType(typeRef);

            // Check that the right case of a class name is used
            if (typeRef.IsObject && typeRef is BoundClassTypeRef ct && ct.Type != null)
            {
                string refName = ct.ClassName.Name.Value;

                if (ct.Type.Kind != SymbolKind.ErrorType)
                {
                    string symbolName = ct.Type.Name;

                    if (refName != symbolName && refName.Equals(symbolName, StringComparison.InvariantCultureIgnoreCase))
                    {
                        // Wrong class name case
                        _diagnostics.Add(_routine, typeRef.PhpSyntax, ErrorCode.INF_ClassNameWrongCase, refName, symbolName);
                    }
                }
            }

            return base.VisitTypeRef(typeRef);
        }

        public override T VisitNew(BoundNewEx x)
        {
            CheckMissusedPrimitiveType(x.TypeRef);

            var type = (TypeSymbol)x.TypeRef.Type;

            if (type.IsValidType())
            {
                if (type.IsInterfaceType())
                {
                    CannotInstantiate(x, "interface", x.TypeRef);
                }
                else if (type.IsStatic)
                {
                    CannotInstantiate(x, "static", x.TypeRef);
                }
                else if (type.IsTraitType())
                {
                    CannotInstantiate(x, "trait", x.TypeRef);
                }
                else // class:
                {
                    // cannot instantiate Closure
                    if (type == DeclaringCompilation.CoreTypes.Closure)
                    {
                        // Instantiation of '{0}' is not allowed
                        Add(x.TypeRef.PhpSyntax.Span, Devsense.PHP.Errors.Errors.ClosureInstantiated, type.Name);
                    }

                    //
                    else if (type.IsAbstract)
                    {
                        // Cannot instantiate abstract class {0}
                        CannotInstantiate(x, "abstract class", x.TypeRef);
                    }
                }
            }

            return base.VisitNew(x);
        }

        public override T VisitReturn(BoundReturnStatement x)
        {
            if (_routine.Syntax is MethodDecl m)
            {
                if (m.Name.Name.IsToStringName)
                {
                    // __tostring() allows only strings to be returned
                    if (x.Returned == null || !IsAllowedToStringReturnType(x.Returned.TypeRefMask))
                    {
                        var span = (x.PhpSyntax != null ? x.PhpSyntax.Span : m.HeadingSpan).ToTextSpan();   // span of return expression OR span of routine header
                        _diagnostics.Add(_routine, span, ErrorCode.WRN_ToStringMustReturnString, _routine.ContainingType.PhpQualifiedName().ToString());
                    }
                }
            }

            // "void" return type hint ?
            if (_routine.SyntaxReturnType is Devsense.PHP.Syntax.Ast.PrimitiveTypeRef pt && pt.PrimitiveTypeName == Devsense.PHP.Syntax.Ast.PrimitiveTypeRef.PrimitiveType.@void)
            {
                if (x.Returned != null)
                {
                    // A void function must not return a value
                    _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.ERR_VoidFunctionCannotReturnValue);
                }
            }

            // do not allow return from "finally" block, not allowed in CLR
            if (x.PhpSyntax != null && IsInScope(Scope.Kind.Finally))
            {
                _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.ERR_NotYetImplemented, "return from 'finally' block");
            }

            //
            return base.VisitReturn(x);
        }

        bool IsAllowedToStringReturnType(TypeRefMask tmask)
        {
            return
                tmask.IsRef ||
                tmask.IsAnyType ||  // dunno
                TypeCtx.IsAString(tmask);

            // anything else (object (even convertible to string), array, number, boolean, ...) is not allowed
        }

        public override T VisitAssign(BoundAssignEx x)
        {
            // Template: <x> = <x>
            if (x.Target is BoundVariableRef lvar && lvar.Variable is LocalVariableReference lloc &&
                x.Value is BoundVariableRef rvar && rvar.Variable is LocalVariableReference rloc &&
                lloc.BoundName == rloc.BoundName && x.PhpSyntax != null)
            {
                // Assignment made to same variable
                _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_AssigningSameVariable);
            }

            // Following is commented since it does not have any effect on the compiler and the type check also needs to be improved.
            // Currently it is very inaccurate:

            //// Check the type of the value assigned to a field against its PHPDoc
            //var valMask = x.Value.TypeRefMask;
            //if (!valMask.IsAnyType && !valMask.IsRef
            //    && x.Target is BoundFieldRef fr && fr.BoundReference.Symbol is SourceFieldSymbol fieldSymbol
            //    && fieldSymbol.FindPhpDocVarTag() is PHPDocBlock.TypeVarDescTag fieldDoc
            //    && fieldDoc.TypeNamesArray.Length != 0)
            //{
            //    var namingCtx = NameUtils.GetNamingContext(fieldSymbol.PHPDocBlock.ContainingType);
            //    var fieldMask = PHPDoc.GetTypeMask(TypeCtx, fieldDoc.TypeNamesArray, namingCtx);

            //    if (!TypeCtx.CanBeSameType(fieldMask, valMask))
            //    {
            //        // The value can't be of the type specified in PHPDoc
            //        _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_FieldPhpDocAssignIncompatible,
            //            TypeCtx.ToString(valMask), fieldSymbol, fieldDoc.TypeNames);
            //    }
            //}

            //

            return base.VisitAssign(x);
        }

        public override T VisitInclude(BoundIncludeEx x)
        {
            // check arguments
            return base.VisitRoutineCall(x);

            // do not check the TargetMethod
        }

        protected override T VisitRoutineCall(BoundRoutineCall x)
        {
            // check arguments
            base.VisitRoutineCall(x);

            // check method
            if (x.TargetMethod.IsValidMethod() && !x.HasArgumentsUnpacking)
            {
                // check mandatory parameters are provided:
                var ps = x.TargetMethod.Parameters;
                var skippedps = 0; // number of implicit parameters provided by compiler
                var expectsmin = 0;

                for (int i = 0; i < ps.Length; i++)
                {
                    if (i == skippedps && ps[i].IsImplicitlyDeclared)
                    {
                        // implicitly provided arguments,
                        // ignored
                        skippedps++;
                    }
                    else
                    {
                        bool isoptional = ps[i].IsOptional || (ps[i] is IPhpValue srcp && srcp.Initializer != null);
                        if (!isoptional && (i < ps.Length - 1 /*check for IsParams only for last parameter*/ || !ps[i].IsParams))
                        {
                            expectsmin = i - skippedps + 1;
                        }
                    }
                }

                var expectsmax = x.TargetMethod.HasParamsParameter()
                    ? int.MaxValue
                    : ps.Length - skippedps;

                //
                var routineName =
                    (x is BoundNewEx)
                    ? "new " + x.TargetMethod.ContainingType.PhpQualifiedName().ToString()
                    : GetMemberNameForDiagnostic(x.TargetMethod, (x.Instance != null || x is BoundStaticFunctionCall));

                //
                if (x.ArgumentsInSourceOrder.Length < expectsmin)
                {
                    _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_MissingArguments, routineName, expectsmin, x.ArgumentsInSourceOrder.Length);
                }
                else if (x.ArgumentsInSourceOrder.Length > expectsmax)
                {
                    _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_TooManyArguments, routineName, expectsmax, x.ArgumentsInSourceOrder.Length);
                }
            }

            return default;
        }

        public override T VisitGlobalFunctionCall(BoundGlobalFunctionCall x)
        {
            CheckUndefinedFunctionCall(x);

            // calling indirectly:
            if (x.Name.IsDirect)
            {
                CheckObsoleteSymbol(x.PhpSyntax, x.TargetMethod, isMemberCall: false);
                CheckGlobalFunctionUsage(x);
            }
            else
            {
                Debug.Assert(x.Name.NameExpression != null);
                // check whether expression can be used as a function callback (must be callable - string, array, object ...)
                if (!TypeHelpers.IsCallable(TypeCtx, x.Name.NameExpression.TypeRefMask))
                {
                    _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.ERR_InvalidFunctionName, TypeCtx.ToString(x.Name.NameExpression.TypeRefMask));
                }
            }

            //
            return base.VisitGlobalFunctionCall(x);
        }

        public override T VisitInstanceFunctionCall(BoundInstanceFunctionCall call)
        {
            // TODO: Consider checking if there are enough situations where this makes sense
            //       (it could only work if IncludeSubclasses is false or the class is final)
            //CheckUndefinedMethodCall(call, call.Instance?.ResultType, call.Name);

            // check target type
            CheckMethodCallTargetInstance(call.Instance, call.Name.NameValue.Name.Value);

            // check deprecated
            CheckObsoleteSymbol(call.PhpSyntax, call.TargetMethod, isMemberCall: true);

            //
            return base.VisitInstanceFunctionCall(call);
        }

        public override T VisitFieldRef(BoundFieldRef x)
        {
            if (x.ContainingType != null)
            {
                // class const
                // static field
                CheckMissusedPrimitiveType(x.ContainingType);
            }

            return base.VisitFieldRef(x);
        }

        public override T VisitCFGCatchBlock(CatchBlock x)
        {
            // TODO: x.TypeRefs -> CheckMissusedPrimitiveType

            return base.VisitCFGCatchBlock(x);
        }

        public override T VisitStaticFunctionCall(BoundStaticFunctionCall call)
        {
            CheckMissusedPrimitiveType(call.TypeRef);

            CheckUndefinedMethodCall(call, call.TypeRef.ResolveTypeSymbol(DeclaringCompilation) as TypeSymbol, call.Name);

            // check deprecated
            CheckObsoleteSymbol(call.PhpSyntax, call.TargetMethod, isMemberCall: true);

            // remember there is call to `parent::__construct`
            if (call.TypeRef is BoundReservedTypeRef rt && rt.ReservedType == ReservedTypeRef.ReservedType.parent &&
                call.Name.IsDirect &&
                call.Name.NameValue.Name.IsConstructName)
            {
                CallsParentCtor = true;
            }

            // check the called method is not abstract
            if (call.TargetMethod.IsValidMethod() && call.TargetMethod.IsAbstract)
            {
                // ERR
                Add(call.PhpSyntax.Span, Devsense.PHP.Errors.Errors.AbstractMethodCalled, call.TargetMethod.ContainingType.PhpName(), call.Name.NameValue.Name.Value);
            }

            //
            return base.VisitStaticFunctionCall(call);
        }

        public override T VisitInstanceOf(BoundInstanceOfEx x)
        {
            CheckMissusedPrimitiveType(x.AsType);

            return base.VisitInstanceOf(x);
        }

        public override T VisitVariableRef(BoundVariableRef x)
        {
            CheckUninitializedVariableUse(x);

            if (x.Access.IsWrite || x.Access.IsUnset)
            {
                // assignment to $this is not allowed:
                if (x.Variable is ThisVariableReference)
                {
                    _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.ERR_CannotAssignToThis);
                }
            }

            return base.VisitVariableRef(x);
        }

        public override T VisitTemporalVariableRef(BoundTemporalVariableRef x)
        {
            // do not make diagnostics on syntesized variables
            return default;
        }

        public override T VisitDeclareStatement(BoundDeclareStatement x)
        {
            _diagnostics.Add(
                _routine,
                ((DeclareStmt)x.PhpSyntax).GetDeclareClauseSpan(),
                ErrorCode.WRN_NotYetImplementedIgnored,
                "Declare construct");

            return base.VisitDeclareStatement(x);
        }

        public override T VisitAssert(BoundAssertEx x)
        {
            base.VisitAssert(x);

            var args = x.ArgumentsInSourceOrder;

            // check number of parameters
            // check whether it is not always false or always true
            if (args.Length >= 1)
            {
                if (args[0].Value.ConstantValue.EqualsOptional(false.AsOptional()))
                {
                    // always failing
                    _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_AssertAlwaysFail);
                }

                if (TypeCtx.IsAString(args[0].Value.TypeRefMask))
                {
                    // deprecated and not supported
                    _diagnostics.Add(_routine, args[0].Value.PhpSyntax, ErrorCode.WRN_StringAssertionDeprecated);
                }

                if (args.Length > 2)
                {
                    // too many args
                    _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_TooManyArguments, "assert", 2, args.Length);
                }
            }
            else
            {
                // assert() expects at least 1 parameter, 0 given
                _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_MissingArguments, "assert", 1, 0);
            }

            return default;
        }

        public override T VisitUnaryExpression(BoundUnaryEx x)
        {
            base.VisitUnaryExpression(x);

            switch (x.Operation)
            {
                case Operations.Clone:
                    // check we only pass object instances to the "clone" operation
                    // anything else causes a runtime warning!
                    var operandTypeMask = x.Operand.TypeRefMask;
                    if (!operandTypeMask.IsAnyType &&
                        !operandTypeMask.IsRef &&
                        !TypeCtx.IsObjectOnly(operandTypeMask))
                    {
                        _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_CloneNonObject, TypeCtx.ToString(operandTypeMask));
                    }
                    break;
            }

            return default;
        }

        public override T VisitBinaryExpression(BoundBinaryEx x)
        {
            base.VisitBinaryExpression(x);

            //

            switch (x.Operation)
            {
                case Operations.Div:
                    if (x.Right.IsConstant())
                    {
                        if (x.Right.ConstantValue.IsZero())
                        {
                            Add(x.Right.PhpSyntax.Span, Devsense.PHP.Errors.Warnings.DivisionByZero);
                        }
                    }
                    break;
            }

            return default;
        }

        public override T VisitConversion(BoundConversionEx x)
        {
            base.VisitConversion(x);

            if (!x.IsImplicit && x.PhpSyntax != null &&
                x.Operand.TypeRefMask.IsSingleType &&
                x.TargetType == TypeCtx.GetTypes(x.Operand.TypeRefMask).FirstOrDefault())
            {
                _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.INF_RedundantCast);
            }

            return default;
        }

        void CheckMethodCallTargetInstance(BoundExpression target, string methodName)
        {
            if (target == null)
            {
                // syntax error (?)
                return;
            }

            string nonobjtype = null;

            if (target.ResultType != null)
            {
                switch (target.ResultType.SpecialType)
                {
                    case SpecialType.System_Void:
                    case SpecialType.System_Int32:
                    case SpecialType.System_Int64:
                    case SpecialType.System_String:
                    case SpecialType.System_Boolean:
                        nonobjtype = target.ResultType.GetPhpTypeNameOrNull();
                        break;
                    default:
                        if (target.ResultType == DeclaringCompilation.CoreTypes.PhpString ||
                            target.ResultType == DeclaringCompilation.CoreTypes.PhpArray ||
                            target.ResultType == DeclaringCompilation.CoreTypes.PhpNumber ||
                            target.ResultType == DeclaringCompilation.CoreTypes.PhpResource ||
                            target.ResultType == DeclaringCompilation.CoreTypes.IPhpArray ||
                            target.ResultType == DeclaringCompilation.CoreTypes.IPhpCallable)
                        {
                            nonobjtype = target.ResultType.GetPhpTypeNameOrNull();
                        }
                        break;
                }
            }
            else
            {
                var tmask = target.TypeRefMask;
                if (!tmask.IsAnyType && !tmask.IsRef && !TypeCtx.IsObject(tmask))
                {
                    nonobjtype = TypeCtx.ToString(tmask);
                }
            }

            //
            if (nonobjtype != null)
            {
                _diagnostics.Add(_routine, target.PhpSyntax, ErrorCode.ERR_MethodCalledOnNonObject, methodName ?? "{}", nonobjtype);
            }
        }

        static string GetMemberNameForDiagnostic(Symbol target, bool isMemberName)
        {
            string name = target.Name;

            if (isMemberName)
            {
                var qname = target.ContainingType.PhpQualifiedName();
                name = qname.ToString(new Name(name), false);
            }

            return name;
        }

        void CheckObsoleteSymbol(LangElement node, Symbol target, bool isMemberCall)
        {
            var obsolete = target?.ObsoleteAttributeData;
            if (obsolete != null)
            {
                _diagnostics.Add(_routine, node, ErrorCode.WRN_SymbolDeprecated, target.Kind.ToString(), GetMemberNameForDiagnostic(target, isMemberCall), obsolete.Message);
            }
        }

        private void CheckUndefinedFunctionCall(BoundGlobalFunctionCall x)
        {
            if (x.Name.IsDirect && x.TargetMethod.IsErrorMethodOrNull())
            {
                var errmethod = (ErrorMethodSymbol)x.TargetMethod;
                if (errmethod != null && errmethod.ErrorKind == ErrorMethodKind.Missing)
                {
                    var span = x.PhpSyntax is FunctionCall fnc ? fnc.NameSpan : x.PhpSyntax.Span;
                    _diagnostics.Add(_routine, span.ToTextSpan(), ErrorCode.WRN_UndefinedFunctionCall, x.Name.NameValue.ToString());
                }
            }
        }

        private void CheckUndefinedMethodCall(BoundRoutineCall x, TypeSymbol type, BoundRoutineName name)
        {
            if (x.TargetMethod is MissingMethodSymbol)
            {
                var span = x.PhpSyntax is FunctionCall fnc ? fnc.NameSpan : x.PhpSyntax.Span;
                _diagnostics.Add(_routine, span.ToTextSpan(), ErrorCode.WRN_UndefinedMethodCall, type.Name, name.NameValue.ToString());
            }
        }

        private void CheckUninitializedVariableUse(BoundVariableRef x)
        {
            if (x.MaybeUninitialized && !x.Access.IsQuiet && x.PhpSyntax != null)
            {
                _diagnostics.Add(_routine, x.PhpSyntax, ErrorCode.WRN_UninitializedVariableUse, x.Name.NameValue.ToString());
            }
        }

        private void CheckUndefinedType(BoundTypeRef typeRef)
        {
            var type = typeRef.ResolvedType;

            // Ignore indirect types (e.g. $foo = new $className())
            if (type.IsErrorTypeOrNull() && !(typeRef is BoundIndirectTypeRef))
            {
                var errtype = typeRef.ResolvedType as ErrorTypeSymbol;
                if (errtype != null && errtype.CandidateReason == CandidateReason.Ambiguous)
                {
                    // type is declared but ambiguously,
                    // warning with declaration ambiguity was already reported, we may skip following
                    return;
                }

                if (typeRef is BoundReservedTypeRef)
                {
                    // unresolved parent, self ?
                }
                else
                {
                    _diagnostics.Add(_routine, typeRef.PhpSyntax, ErrorCode.WRN_UndefinedType, typeRef.ToString());
                }
            }

            // undefined "parent"
            if (typeRef is BoundReservedTypeRef reservedType && reservedType.ReservedType == ReservedTypeRef.ReservedType.parent && typeRef.PhpSyntax != null)
            {
                var typeCtx = _routine.ContainingType as SourceTypeSymbol;
                if ((typeCtx != null && typeCtx.IsTrait) || _routine.IsGlobalScope)
                {
                    // global code or trait -> resolved at run time
                }
                else if (typeCtx == null || typeCtx.Syntax.BaseClass == null)
                {
                    // in a global function or a class without parent -> error
                    Add(typeRef.PhpSyntax.Span, Devsense.PHP.Errors.FatalErrors.ParentAccessedInParentlessClass);
                }
            }
        }

        private void CheckGlobalFunctionUsage(BoundGlobalFunctionCall call)
        {
            if (!AnalysisFacts.HasSimpleName(call, out string name))
            {
                return;
            }

            switch (name)
            {
                case "printf":
                case "sprintf":
                    // Check that the number of arguments matches the format string
                    if (!call.ArgumentsInSourceOrder.IsEmpty && call.ArgumentsInSourceOrder[0].Value.ConstantValue.TryConvertToString(out string format))
                    {
                        int posSpecCount = 0;
                        int numSpecMax = 0;
                        foreach (Match match in PrintfSpecsRegex.Matches(format))
                        {
                            var numSpecStr = match.Groups[1].Value;
                            if (numSpecStr == string.Empty)
                            {
                                // %d
                                posSpecCount++;
                            }
                            else
                            {
                                // %2$d
                                int numSpec = int.Parse(numSpecStr);
                                numSpecMax = Math.Max(numSpec, numSpecMax);
                            }
                        }

                        int expectedArgCount = 1 + Math.Max(posSpecCount, numSpecMax);

                        if (call.ArgumentsInSourceOrder.Length != expectedArgCount)
                        {
                            // Wrong number of arguments with respect to the format string
                            _diagnostics.Add(_routine, call.PhpSyntax, ErrorCode.WRN_FormatStringWrongArgCount, name);
                        }
                    }
                    break;
            }
        }

        public override T VisitCFGTryCatchEdge(TryCatchEdge x)
        {
            // remember scopes,
            // .Accept() on BodyBlocks traverses not only the try block but also the rest of the code

            WithScope(new Scope
            {
                ScopeKind = Scope.Kind.Try,
                From = x.BodyBlock.Ordinal,
                To = x.NextBlock.Ordinal
            });

            for (int i = 0; i < x.CatchBlocks.Length; i++)
            {
                WithScope(new Scope
                {
                    ScopeKind = Scope.Kind.Catch,
                    From = x.CatchBlocks[i].Ordinal,
                    To = ((i + 1 < x.CatchBlocks.Length) ? x.CatchBlocks[i + 1] : x.FinallyBlock ?? x.NextBlock).Ordinal,
                });
            }

            if (x.FinallyBlock != null)
            {
                WithScope(new Scope
                {
                    ScopeKind = Scope.Kind.Finally,
                    From = x.FinallyBlock.Ordinal,
                    To = x.NextBlock.Ordinal
                });
            }

            // visit:

            return base.VisitCFGTryCatchEdge(x);
        }

        public override T VisitStaticStatement(BoundStaticVariableStatement x)
        {
            return base.VisitStaticStatement(x);
        }

        public override T VisitYieldStatement(BoundYieldStatement boundYieldStatement)
        {
            if (IsInTryCatchScope())
            {
                // TODO: Start supporting yielding from exception handling constructs.
                _diagnostics.Add(_routine, boundYieldStatement.PhpSyntax, ErrorCode.ERR_NotYetImplemented, "Yielding from an exception handling construct (try, catch, finally)");
            }

            return default;
        }

        public override T VisitCFGForeachEnumereeEdge(ForeachEnumereeEdge x)
        {
            base.VisitCFGForeachEnumereeEdge(x);

            var enumereeTypeMask = x.Enumeree.TypeRefMask;
            if (!enumereeTypeMask.IsAnyType && !enumereeTypeMask.IsRef)
            {
                // Apart from array, any object can possibly implement Traversable, hence no warning for them
                var types = TypeCtx.GetTypes(enumereeTypeMask);
                if (!types.Any(t => t.IsArray || t.IsObject))   // Using !All causes too many false positives (due to explode(..) etc.)
                {
                    // Using non-iterable type for enumeree
                    _diagnostics.Add(_routine, x.Enumeree.PhpSyntax, ErrorCode.WRN_ForeachNonIterable, TypeCtx.ToString(enumereeTypeMask));
                }
            }

            return default;
        }
    }
}
