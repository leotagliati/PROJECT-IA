# Empacota um run PARADO para continuar o treino em outro PC (o results/ é gitignored).
#
# Uso (Anaconda Prompt, ambiente mlagents, na raiz do projeto, DEPOIS do Ctrl+C no treino):
#   python tools/pack_run.py v8.2_mapa_04
#
# Grava:
#   TrainingRuns/<run>_<passos>M.zip   checkpoint.pt, configuration.yaml, training_status.json e os eventos do
#                                     TensorBoard: o mínimo para o --resume (o mesmo conteúdo dos zips da v8.1)
#   Assets/Brains/<Behavior>/<Behavior>_<versão>_<passos>M.onnx   o .onnx final do run (o do Ctrl+C), para testar
# e imprime os comandos de git. No outro PC: extrair o zip na raiz do projeto, refazer o build (Builds/ também é
# gitignored) e rodar o mesmo comando de treino com --resume.
import glob
import json
import os
import shutil
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main():
    if len(sys.argv) < 2:
        sys.exit("uso: python tools/pack_run.py <run-id>   (ex.: v8.2_mapa_04)")

    run = sys.argv[1]
    run_dir = os.path.join(ROOT, "results", run)
    status_path = os.path.join(run_dir, "run_logs", "training_status.json")
    if not os.path.isfile(status_path):
        sys.exit(f"{run}: sem {status_path} (o run existe? já foi parado com Ctrl+C?)")

    status = json.load(open(status_path, encoding="utf-8"))
    behaviors = [k for k, v in status.items() if isinstance(v, dict) and "checkpoints" in v]
    if not behaviors:
        sys.exit(f"{run}: training_status.json sem checkpoints")

    behavior = behaviors[0]
    final = status[behavior].get("final_checkpoint") or status[behavior]["checkpoints"][-1]
    steps = int(final["steps"])
    label = f"{steps / 1e6:.1f}M".replace(".0M", "M")

    checkpoint = os.path.join(run_dir, behavior, "checkpoint.pt")
    if not os.path.isfile(checkpoint):
        sys.exit(f"{run}: sem {checkpoint}")

    # Zip com os caminhos a partir da raiz (results/<run>/...): no outro PC é só extrair na raiz do projeto.
    files = [checkpoint, status_path, os.path.join(run_dir, "configuration.yaml")]
    files += glob.glob(os.path.join(run_dir, behavior, "events.out.tfevents.*"))
    os.makedirs(os.path.join(ROOT, "TrainingRuns"), exist_ok=True)
    zip_path = os.path.join(ROOT, "TrainingRuns", f"{run}_{label}.zip")
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        for path in files:
            if os.path.isfile(path):
                z.write(path, os.path.relpath(path, ROOT).replace("\\", "/"))

    # O .onnx final do run (exportado no Ctrl+C) vai para Assets/Brains, com a versão do run-id (v8.2_mapa_04 -> v8.2).
    onnx_src = os.path.join(run_dir, f"{behavior}.onnx")
    version = run.split("_")[0]
    onnx_rel = f"Assets/Brains/{behavior}/{behavior}_{version}_{label}.onnx"
    if os.path.isfile(onnx_src):
        if os.path.isfile(onnx_src + ".data"):
            print(f"AVISO: {onnx_src} tem os pesos num .onnx.data separado; o Unity não carrega. Junte antes.")
        os.makedirs(os.path.dirname(os.path.join(ROOT, onnx_rel)), exist_ok=True)
        shutil.copyfile(onnx_src, os.path.join(ROOT, onnx_rel))
    else:
        onnx_rel = None
        print(f"AVISO: sem {onnx_src} (o run não foi parado com Ctrl+C?); só o zip foi gravado.")

    zip_rel = os.path.relpath(zip_path, ROOT).replace("\\", "/")
    print(f"\nRun {run}, {behavior}, parado em {steps:,} steps.")
    print(f"  {zip_rel}  ({os.path.getsize(zip_path) // 1024} KB)")
    if onnx_rel:
        print(f"  {onnx_rel}")
    print("\nAntes do commit: abra (ou clique) o Unity para ele criar o .meta do .onnx. Depois:")
    print(f'  git add "{zip_rel}"' + (f' "{onnx_rel}" "{onnx_rel}.meta"' if onnx_rel else ""))
    print(f'  git commit -m "Add {version} {behavior} model and training state at {label} for resuming on another PC"')
    print("  git push")


if __name__ == "__main__":
    main()
