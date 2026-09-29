# Pega o MELHOR cérebro do run mais recente e deixa pronto em Assets/ para arrastar no agente.
#
# Uso (Anaconda Prompt, com o ambiente mlagents ativo, na raiz do projeto):
#   python tools/best_onnx.py                      # run mais recente em results/
#   python tools/best_onnx.py node4_full_01        # um run específico
#   python tools/best_onnx.py --metric Exploration/Coverage
#   python tools/best_onnx.py --list               # só mostra a tabela, não copia nada
#
# Como escolhe:
#   1. Só checkpoints da LIÇÃO MAIS AVANÇADA que o run alcançou. Reward de lições diferentes não
#      se compara: a Perto paga diferente da Patrulha, e o "melhor reward" do run inteiro quase
#      sempre é um checkpoint velho de uma lição fácil.
#   2. Dentro dela, a maior MÉDIA da métrica no TensorBoard nos --window steps antes do
#      checkpoint. A nota que o ML-Agents grava em training_status.json é um ponto só e oscila
#      demais (no node4_noarrow_01 ela variava de -6.1 a -2.3 entre checkpoints vizinhos).
#   3. Empate (diferença < --tie) fica com o mais RECENTE.
#
# Saída: Assets/<Behavior>_<run>_<steps>.onnx em arquivo ÚNICO. O exportador do ML-Agents com
# torch novo grava os pesos num .onnx.data separado, que o import do Unity pode não achar — aqui
# os dois são juntados antes de copiar.
import argparse, glob, json, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RESULTS = os.path.join(ROOT, "results")


def latest_run():
    runs = [d for d in glob.glob(os.path.join(RESULTS, "*")) if os.path.isfile(os.path.join(d, "run_logs", "training_status.json"))]
    if not runs:
        sys.exit("Nenhum run com run_logs/training_status.json em results/.")
    return os.path.basename(max(runs, key=lambda d: os.path.getmtime(os.path.join(d, "run_logs", "training_status.json"))))


def load_scalars(behavior_dir):
    # Todos os arquivos de evento: um --resume cria outro, e a curva fica espalhada.
    from tensorboard.backend.event_processing.event_accumulator import EventAccumulator
    series = {}
    for path in sorted(glob.glob(os.path.join(behavior_dir, "events.out.tfevents*"))):
        ea = EventAccumulator(path, size_guidance={"scalars": 0})
        ea.Reload()
        for tag in ea.Tags()["scalars"]:
            series.setdefault(tag, []).extend((e.step, e.value) for e in ea.Scalars(tag))
    return series


def window_mean(points, lo, hi):
    values = [v for s, v in points if lo < s <= hi]
    return sum(values) / len(values) if values else None


def lesson_at(series, lo, hi):
    # Maior número de lição entre todos os parâmetros de currículo na janela.
    best = None
    for tag, points in series.items():
        if tag.startswith("Environment/Lesson Number"):
            values = [v for s, v in points if lo < s <= hi]
            if values:
                best = max(best or 0, max(values))
    return best


def main():
    # Console do Windows nem sempre é UTF-8: acento vira '?' em vez de derrubar o script.
    sys.stdout.reconfigure(errors="replace")

    parser = argparse.ArgumentParser()
    parser.add_argument("run", nargs="?", help="run-id (padrão: o mais recente em results/)")
    parser.add_argument("--behavior", default="GraphExplorer")
    parser.add_argument("--metric", default="Environment/Cumulative Reward")
    parser.add_argument("--window", type=int, default=500_000, help="steps de média antes do checkpoint")
    parser.add_argument("--tie", type=float, default=0.05, help="diferença que conta como empate")
    parser.add_argument("--list", action="store_true", help="só mostra a tabela")
    args = parser.parse_args()

    run = args.run or latest_run()
    run_dir = os.path.join(RESULTS, run)
    behavior_dir = os.path.join(run_dir, args.behavior)
    status = json.load(open(os.path.join(run_dir, "run_logs", "training_status.json"), encoding="utf-8"))
    checkpoints = status.get(args.behavior, {}).get("checkpoints", [])
    checkpoints = [c for c in checkpoints if os.path.isfile(os.path.join(ROOT, c["file_path"]))]
    if not checkpoints:
        sys.exit(f"{run}: nenhum checkpoint .onnx de {args.behavior} no disco.")

    series = load_scalars(behavior_dir)
    if args.metric not in series:
        sys.exit(f"Métrica '{args.metric}' não existe. Disponíveis: {', '.join(sorted(series))}")

    rows = []
    for c in checkpoints:
        step = c["steps"]
        lo = step - args.window
        rows.append({
            "step": step,
            "path": os.path.join(ROOT, c["file_path"]),
            "mean": window_mean(series[args.metric], lo, step),
            "lesson": lesson_at(series, lo, step),
        })

    top_lesson = max((r["lesson"] for r in rows if r["lesson"] is not None), default=None)
    candidates = [r for r in rows if r["mean"] is not None and (top_lesson is None or r["lesson"] == top_lesson)]
    if not candidates:
        sys.exit("Nenhum checkpoint com dados da métrica na janela — rode mais tempo ou diminua --window.")

    best_mean = max(r["mean"] for r in candidates)
    best = max((r for r in candidates if r["mean"] >= best_mean - args.tie), key=lambda r: r["step"])

    print(f"Run: {run}   métrica: {args.metric} (média dos {args.window:,} steps anteriores)")
    print(f"{'step':>12}  {'lição':>5}  {'média':>9}")
    for r in sorted(rows, key=lambda r: r["step"]):
        mark = "  <- escolhido" if r is best else ""
        lesson = "-" if r["lesson"] is None else f"{r['lesson']:.0f}"
        mean = "-" if r["mean"] is None else f"{r['mean']:.3f}"
        print(f"{r['step']:>12,}  {lesson:>5}  {mean:>9}{mark}")

    if args.list:
        return

    import onnx
    model = onnx.load(best["path"], load_external_data=True)
    name = f"{args.behavior}_{run}_{best['step'] // 1000}k.onnx"
    out = os.path.join(ROOT, "Assets", name)
    onnx.save_model(model, out, save_as_external_data=False)
    onnx.checker.check_model(onnx.load(out))

    inputs = ", ".join(f"{i.name}={[d.dim_value for d in i.type.tensor_type.shape.dim][1:]}" for i in model.graph.input)
    print(f"\nSalvo: Assets/{name}  ({os.path.getsize(out) // 1024} KB, entradas {inputs})")
    print(f"No Unity: arraste no campo Model do Behavior Parameters do agente ({args.behavior}).")


if __name__ == "__main__":
    main()
