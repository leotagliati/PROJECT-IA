# Pega o checkpoint MAIS NOVO de um run da v8 e grava em Assets/FreeExplorer_v8_watch.onnx (nome fixo, sobrescrito),
# para o menu PROJECT-IA > "v8 (em Play): assistir o cérebro" achar sempre no mesmo lugar. Para escolher o MELHOR
# checkpoint (e não o mais novo), use o tools/best_onnx.py --behavior FreeExplorer.
#
# Uso (ambiente mlagents, na raiz do projeto), ou pelo menu PROJECT-IA > "v8: preparar cérebro do treino":
#   python tools/v8_watch_onnx.py                  # run v8.0_zero_01
#   python tools/v8_watch_onnx.py v8.0_zero_02
#
# O exportador do ML-Agents com torch novo pode gravar os pesos num .onnx.data ao lado; aqui os dois são juntados num
# arquivo só (sem isso o Unity importa o modelo sem pesos e o monstro não se mexe).
import glob, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "Assets", "FreeExplorer_v8_watch.onnx")


def main():
    run = sys.argv[1] if len(sys.argv) > 1 else "v8.0_zero_01"
    folder = os.path.join(ROOT, "results", run, "FreeExplorer")
    checkpoints = []
    for path in glob.glob(os.path.join(folder, "FreeExplorer-*.onnx")):
        match = re.search(r"FreeExplorer-(\d+)\.onnx$", path)
        if match:
            checkpoints.append((int(match.group(1)), path))

    # Sem checkpoint numerado ainda: o FreeExplorer.onnx do fim de um run parado com Ctrl+C.
    final = os.path.join(folder, "FreeExplorer.onnx")
    if not checkpoints and os.path.isfile(final):
        checkpoints.append((-1, final))

    if not checkpoints:
        sys.exit(f"Nenhum checkpoint em {folder} ainda (o primeiro sai em 500k steps).")

    steps, path = max(checkpoints)
    import onnx
    model = onnx.load(path, load_external_data=True)
    onnx.save_model(model, OUT, save_as_external_data=False)
    label = f"{steps:,} steps".replace(",", ".") if steps >= 0 else "modelo final"
    print(f"OK: {os.path.basename(path)} ({label}) -> Assets/FreeExplorer_v8_watch.onnx")


if __name__ == "__main__":
    main()
