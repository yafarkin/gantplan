using Google.OrTools.Sat;

var model = new CpModel();

int horizon = 20; // запас по времени

// длительность задачи (рабочих дней)
int taskDuration = 10;

// старт и конец задачи
var start = model.NewIntVar(0, horizon, "start");
var end   = model.NewIntVar(0, horizon, "end");

// сама задача
var task = model.NewIntervalVar(start, taskDuration, end, "task");

// --- выходные ---
// укажем их как фиксированные интервалы
var w1Start = model.NewConstant(5);
var w1End   = model.NewConstant(6);
var weekend1 = model.NewIntervalVar(w1Start, 1, w1End, "day5_off");

var w2Start = model.NewConstant(6);
var w2End   = model.NewConstant(7);
var weekend2 = model.NewIntervalVar(w2Start, 1, w2End, "day6_off");

// --- ограничения ---
// 1. конец = старт + длительность
model.Add(end == start + taskDuration);

// 2. задача не пересекается с выходными
model.AddNoOverlap(new List<IntervalVar> { task, weekend1, weekend2 });

// --- решаем ---
var solver = new CpSolver();
var status = solver.Solve(model);

if (status is CpSolverStatus.Optimal or CpSolverStatus.Feasible)
{
    Console.WriteLine($"Task start: {solver.Value(start)}");
    Console.WriteLine($"Task end:   {solver.Value(end)}");
}
else
{
    Console.WriteLine("No solution found.");
}