using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

internal interface IHighwayEditCommand
{
    bool Execute(IHistoryService history);
}
