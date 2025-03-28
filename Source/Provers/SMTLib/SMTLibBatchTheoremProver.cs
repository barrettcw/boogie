using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using Microsoft.Boogie.VCExprAST;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Boogie.SMTLib
{
  /// <summary>
  /// This class provides a "batch" interface to an SMT-Lib prover that
  /// prepares all input up-front, sends it to the prover, and then reads
  /// all output from the prover.
  ///
  /// Some SMT-Lib provers don't support the interactive (a.k.a.
  /// incremental) mode provided by SMTLibInteractiveTheoremProver. This
  /// class allows Boogie to work with such provers, and also works with
  /// provers that support interactive modes (including CVC5, Yices2, and
  /// Z3). To work correctly in batch mode, a solver must  be able to
  /// handle the following commands without crashing:
  ///
  ///   * `(get-model)` after returning `unsat`
  ///   * `(get-info :reason-unknown)` after returning `sat` or `unsat`
  ///
  /// Working non-interactively precludes certain features, including the
  /// ability to return multiple errors. The current implementation also
  /// does not support evaluating expressions in the result context,
  /// generating unsat cores, or checking assumptions.
  /// </summary>
  public class SMTLibBatchTheoremProver : SMTLibProcessTheoremProver
  {
    private bool CheckSatSent;
    private int resourceCount;
    private Model errorModel;

    [NotDelayed]
    public SMTLibBatchTheoremProver(SMTLibOptions libOptions, ProverOptions options, VCExpressionGenerator gen,
      SMTLibProverContext ctx) : base(libOptions, options, gen, ctx)
    {
      if (usingUnsatCore) {
        throw new NotSupportedException("Batch mode solver interface does not support unsat cores.");
      }
    }

    public override Task GoBackToIdle()
    {
      return Task.CompletedTask;
    }

    public override int FlushAxiomsToTheoremProver()
    {
      // we feed the axioms when BeginCheck is called.
      return 0;
    }

    public override Task BeginCheck(string descriptiveName, VCExpr vc, ErrorHandler handler)
    {
      SetupProcess();
      CheckSatSent = false;
      FullReset(gen);

      if (options.LogFilename != null && currentLogFile == null) {
        currentLogFile = OpenOutputFile(descriptiveName);
        currentLogFile.Write(common.ToString());
      }

      PrepareCommon();
      FlushAxioms();

      OptimizationRequests.Clear();

      string vcString = "(assert (not\n" + VCExpr2String(vc, 1) + "\n))";
      FlushAxioms();

      Push();
      SendVCAndOptions(descriptiveName, vcString);
      SendOptimizationRequests();

      FlushLogFile();

      Process.NewProblem(descriptiveName);

      SendCheckSat();
      Pop();

      FlushLogFile();
      return Task.CompletedTask;
    }

    public override Task Reset(VCExpressionGenerator generator) {
      return Task.CompletedTask;
    }

    public override void FullReset(VCExpressionGenerator generator)
    {
      this.gen = generator;
      common.Clear();
      SetupAxiomBuilder(gen);
      Axioms.Clear();
      TypeDecls.Clear();
      AxiomsAreSetup = false;
      DeclCollector.Reset();
      NamedAssumes.Clear();
      UsedNamedAssumes = null;
    }

    // TODO: move to base?
    [NoDefaultContract]
    public override async Task<Outcome> CheckOutcome(ErrorHandler handler, int errorLimit, CancellationToken cancellationToken)
    {
      var result = await CheckOutcomeCore(handler, cancellationToken, errorLimit);

      FlushLogFile();

      return result;
    }

    [NoDefaultContract]
    public override async Task<Outcome> CheckOutcomeCore(ErrorHandler handler, CancellationToken cancellationToken,
      int errorLimit)
    {
      if (Process == null || proverErrors.Count > 0) {
        return Outcome.Undetermined;
      }

      try {
        currentErrorHandler = handler;
        FlushProverWarnings();

        var result = await GetResponse(cancellationToken);

        if (result == Outcome.Invalid) {
          var labels = CalculatePath(handler.StartingProcId(), errorModel);
          if (labels.Length == 0) {
            // Without a path to an error, we don't know what to report
            result = Outcome.Undetermined;
          } else {
            handler.OnModel(labels, errorModel, result);
          }
        }

        // Note: batch mode never tries to discover more errors

        FlushLogFile();

        Close();

        return result;
      } finally {
        Close();
        currentErrorHandler = null;
      }
    }

    private async Task<Outcome> GetResponse(CancellationToken cancellationToken)
    {
      var outcomeSExp = await Process.GetProverResponse().WaitAsync(cancellationToken);
      var result = ParseOutcome(outcomeSExp, out var wasUnknown);

      var unknownSExp = await Process.GetProverResponse().WaitAsync(cancellationToken);
      if (wasUnknown) {
        result = ParseReasonUnknown(unknownSExp, result);
      }

      if (options.Solver == SolverKind.Z3) {
        var rlimitSExp = await Process.GetProverResponse().WaitAsync(cancellationToken);
        resourceCount = ParseRCount(rlimitSExp);
      }

      var modelSExp = await Process.GetProverResponse().WaitAsync(cancellationToken);
      errorModel = ParseErrorModel(modelSExp);

      return result;
    }

    // Note: This could probably be made to work with the result of
    // `(get-value ControlFlow)` rather than the full result of `(get-model)`.
    // At some point we should do experiments to see whether that's at all
    // faster.
    private string[] CalculatePath(int controlFlowConstant, Model model)
    {
      var path = new List<string>();

      if (model is null) {
        return path.ToArray();
      }

      var function = model.TryGetFunc("ControlFlow");
      var controlFlowElement = model.TryMkElement(controlFlowConstant.ToString());
      var zeroElement = model.TryMkElement("0");
      var v = zeroElement;

      while (true) {
        var result = function?.TryEval(controlFlowElement, v) ?? function?.Else;
        if (result is null) {
          break;
        }
        var resultData = result as Model.DatatypeValue;

        if (resultData is not null && resultData.Arguments.Length >= 1) {
          path.Add(resultData.Arguments[0].ToString());
          break;
        } else if (result is Model.Integer) {
          path.Add(result.ToString());
        } else {
          HandleProverError($"Invalid control flow model received from solver.");
          break;
        }

        v = result;
      }

      return path.ToArray();
    }

    public override Task<object> Evaluate(VCExpr expr)
    {
      throw new NotSupportedException("Batch mode solver interface does not support evaluating SMT expressions.");
    }

    public override void Check()
    {
      throw new NotSupportedException("Batch mode solver interface does not support the Check call.");
    }

    private void SendCheckSat()
    {
      UsedNamedAssumes = null;
      SendThisVC("(check-sat)");
      SendThisVC("(get-info :reason-unknown)");
      if (options.Solver == SolverKind.Z3) {
        SendThisVC($"(get-info :{Z3.RlimitOption})");
      }
      SendThisVC("(get-model)");
      CheckSatSent = true;
      Process.IndicateEndOfInput();
    }

    protected override void Send(string s, bool isCommon)
    {
      s = Sanitize(s);

      if (isCommon) {
        common.Append(s).Append("\r\n");
      }

      // Boogie emits comments after the solver has responded. In batch
      // mode, sending these to the solver is problematic. But they'll
      // still get sent to the log below.
      if (Process != null && !CheckSatSent) {
        Process.Send(s);
      }

      if (currentLogFile != null) {
        currentLogFile.WriteLine(s);
        currentLogFile.Flush();
      }
    }

    public override Task<int> GetRCount()
    {
      return Task.FromResult(resourceCount);
    }

    public override List<string> UnsatCore()
    {
      throw new NotSupportedException("Batch mode solver interface does not support unsat cores.");
    }

    public override Task<(Outcome, List<int>)> CheckAssumptions(List<VCExpr> assumptions,
      ErrorHandler handler, CancellationToken cancellationToken)
    {
      throw new NotSupportedException("Batch mode solver interface does not support checking assumptions.");
    }

    public override Task<(Outcome, List<int>)> CheckAssumptions(List<VCExpr> hardAssumptions, List<VCExpr> softAssumptions,
      ErrorHandler handler, CancellationToken cancellationToken)
    {
      throw new NotSupportedException("Batch mode solver interface does not support checking assumptions.");
    }
  }
}
