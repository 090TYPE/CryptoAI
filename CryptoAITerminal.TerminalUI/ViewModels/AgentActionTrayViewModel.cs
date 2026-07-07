using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services.AppActions;
using ReactiveUI;

namespace CryptoAITerminal.TerminalUI.ViewModels;

public sealed class AgentActionProposalViewModel : ReactiveObject
{
    public AgentActionProposalViewModel(ActionProposal proposal) { Proposal = proposal; }
    public ActionProposal Proposal { get; }
    public string ActionId => Proposal.ActionId;
    public string Preview => Proposal.Preview;
}

/// <summary>Holds pending mutating proposals; Approve executes via the injected executor, Reject drops.</summary>
public sealed class AgentActionTrayViewModel : ReactiveObject
{
    private readonly Func<ActionProposal, Task<AppActionResult>> _execute;
    public AgentActionTrayViewModel(Func<ActionProposal, Task<AppActionResult>> execute) => _execute = execute;

    public ObservableCollection<AgentActionProposalViewModel> Pending { get; } = new();
    public bool HasPending => Pending.Count > 0;

    public void Enqueue(ActionProposal p)
    {
        Pending.Add(new AgentActionProposalViewModel(p));
        this.RaisePropertyChanged(nameof(HasPending));
    }

    public async Task<AppActionResult> ApproveAsync(AgentActionProposalViewModel vm)
    {
        Pending.Remove(vm);
        this.RaisePropertyChanged(nameof(HasPending));
        return await _execute(vm.Proposal);
    }

    public void Reject(AgentActionProposalViewModel vm)
    {
        Pending.Remove(vm);
        this.RaisePropertyChanged(nameof(HasPending));
    }
}
