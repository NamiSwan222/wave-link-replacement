"""Exports ContentVec (the speech-content model every RVC voice uses) to ONNX for WaveFix.

Input:  source [1, N] float, 16 kHz mono audio (not normalized, as RVC feeds it).
Outputs: v1 [1, T, 256]  final_proj(hidden_states[9])  (what RVC v1 models expect)
         v2 [1, T, 768]  last hidden state (layer 12)  (what RVC v2 models expect)
T = 50 frames per second (one per 320 samples).

The weights are https://huggingface.co/lengyue233/content-vec-best (MIT), the Transformers
conversion RVC itself loads. Run once on a dev machine:
    python tools/rvc/make_contentvec.py --model <dir with config.json + pytorch_model.bin> --out contentvec.onnx
Needs: torch (CPU is fine), transformers, onnx, onnxruntime, numpy.
"""

import argparse

import numpy as np
import torch
from torch import nn
from transformers import HubertConfig, HubertModel


class HubertModelWithFinalProj(HubertModel):
    def __init__(self, config):
        super().__init__(config)
        self.final_proj = nn.Linear(config.hidden_size, config.classifier_proj_size)


class Export(nn.Module):
    def __init__(self, model):
        super().__init__()
        self.model = model

    def forward(self, source):
        out = self.model(source, output_hidden_states=True, return_dict=True)
        return self.model.final_proj(out.hidden_states[9]), out.last_hidden_state


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    model = HubertModelWithFinalProj.from_pretrained(args.model, attn_implementation="eager").float().eval()
    wrapper = Export(model).eval()
    example = torch.randn(1, 16000 * 2) * 0.1
    torch.onnx.export(
        wrapper, (example,), args.out, input_names=["source"], output_names=["v1", "v2"],
        dynamic_axes={"source": {1: "N"}, "v1": {1: "T"}, "v2": {1: "T"}},
        opset_version=17, do_constant_folding=True, dynamo=False,
    )

    # Check against PyTorch on audio of a different length than the export example.
    import onnxruntime as ort
    sess = ort.InferenceSession(args.out, providers=["CPUExecutionProvider"])
    rng = np.random.default_rng(3)
    t = np.arange(16000 * 3 + 123) / 16000
    audio = (0.3 * np.sin(2 * np.pi * 180 * t) * (1 + np.sin(2 * np.pi * 3 * t)) + 0.02 * rng.standard_normal(t.size))
    audio = audio.astype(np.float32)[None]
    with torch.no_grad():
        ref = [r.numpy() for r in wrapper(torch.from_numpy(audio))]
    got = sess.run(None, {"source": audio})
    for name, r, g in zip(["v1", "v2"], ref, got):
        err = np.abs(r - g).max()
        print(f"{name}: shape {g.shape}, max |diff| vs PyTorch {err:.2e}")
        assert r.shape == g.shape and err < 1e-3, name


if __name__ == "__main__":
    main()
