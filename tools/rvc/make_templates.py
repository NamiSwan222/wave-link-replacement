"""Builds the RVC synthesizer templates WaveFix uses to run RVC voice models without Python.

A template is RVC's synthesizer exported to ONNX with every learned weight left empty (names and
shapes only). Weight norm stays in the graph, so the weight names are exactly the .pth's keys. When you import a .pth voice model, WaveFix (src/WaveFix/Rvc/RvcModelImport.cs)
reads its weights and fills them into the matching template, so users never need PyTorch.

Run once per RVC release (dev machines only):
    python tools/rvc/make_templates.py --rvc <RVC checkout> --out src/WaveFix/Rvc/Templates
    python tools/rvc/make_templates.py --rvc <RVC checkout> --out <dir> --test   # + test fixtures

<RVC checkout> is https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI (MIT) at
commit 81eed5e8f68b6bed1789f682fe78cdd324495afc; only infer/module/*.py is used.
Needs: torch (CPU is fine), onnx, numpy.
"""

import argparse
import json
import os
import sys

import numpy as np
import torch
from torch import nn

# Architecture of each standard RVC model type: the "config" list RVC stores in every .pth
# (spec_channels, segment_size, inter, hidden, filter, heads, layers, kernel, dropout, resblock,
#  resblock_kernels, resblock_dilations, upsample_rates, upsample_initial, upsample_kernels,
#  speakers, gin_channels, sample_rate). Speakers vary per model and don't change the graph.
CONFIGS = {
    "v1-32k": ("v1", [513, 32, 192, 192, 768, 2, 6, 3, 0, "1", [3, 7, 11], [[1, 3, 5]] * 3, [10, 4, 2, 2, 2], 512, [16, 16, 4, 4, 4], 109, 256, 32000]),
    "v1-40k": ("v1", [1025, 32, 192, 192, 768, 2, 6, 3, 0, "1", [3, 7, 11], [[1, 3, 5]] * 3, [10, 10, 2, 2], 512, [16, 16, 4, 4], 109, 256, 40000]),
    "v1-48k": ("v1", [1025, 32, 192, 192, 768, 2, 6, 3, 0, "1", [3, 7, 11], [[1, 3, 5]] * 3, [10, 6, 2, 2, 2], 512, [16, 16, 4, 4, 4], 109, 256, 48000]),
    "v2-32k": ("v2", [513, 32, 192, 192, 768, 2, 6, 3, 0, "1", [3, 7, 11], [[1, 3, 5]] * 3, [10, 8, 2, 2], 512, [20, 16, 4, 4], 109, 256, 32000]),
    "v2-40k": ("v2", [1025, 32, 192, 192, 768, 2, 6, 3, 0, "1", [3, 7, 11], [[1, 3, 5]] * 3, [10, 10, 2, 2], 512, [16, 16, 4, 4], 109, 256, 40000]),
    "v2-48k": ("v2", [1025, 32, 192, 192, 768, 2, 6, 3, 0, "1", [3, 7, 11], [[1, 3, 5]] * 3, [12, 10, 2, 2], 512, [24, 20, 4, 4], 109, 256, 48000]),
}


def make_attention_traceable():
    """RVC's relative-position attention turns the sequence length into Python ints (max(), int()),
    so a traced export only works at the length it was traced with. These replacements compute
    exactly the same thing with index tensors, so the exported graph works at any length."""
    import infer.module.attentions as A

    def get_relative_embeddings(self, rel, length):
        # Window of 2*length-1 relative positions centred on 0, zero outside the learned 2w+1.
        w = self.window_size
        pos = torch.arange(2 * length - 1, device=rel.device) - (length - 1) + w
        valid = ((pos >= 0) & (pos <= 2 * w)).to(rel.dtype)
        return rel[:, pos.clamp(0, 2 * w)] * valid[None, :, None]

    def rel_to_abs(self, x):
        # [b, h, l, 2l-1] -> [b, h, l, l]: out[i, j] = x[i, j - i + l - 1]
        b, h, l, _ = x.size()
        ar = torch.arange(l, device=x.device)
        idx = (ar[None, :] - ar[:, None] + (l - 1)).expand(b, h, l, l)
        return torch.gather(x, 3, idx)

    def abs_to_rel(self, x):
        # [b, h, l, l] -> [b, h, l, 2l-1]: out[i, k] = x[i, k + i - (l - 1)] where in range, else 0
        b, h, l, _ = x.size()
        i = torch.arange(l, device=x.device)[:, None]
        k = torch.arange(2 * l - 1, device=x.device)[None, :]
        src = k + i - (l - 1)
        valid = ((src >= 0) & (src < l)).to(x.dtype)
        out = torch.gather(x, 3, src.clamp(0, l - 1).expand(b, h, l, 2 * l - 1))
        return out * valid

    A.MultiHeadAttention._get_relative_embeddings = get_relative_embeddings
    A.MultiHeadAttention._relative_position_to_absolute_position = rel_to_abs
    A.MultiHeadAttention._absolute_position_to_relative_position = abs_to_rel


def check_attention_patch():
    """The replacements must give the same numbers as RVC's originals at every length."""
    import importlib
    import infer.module.attentions as A
    orig = importlib.reload(A).MultiHeadAttention
    saved = (orig._get_relative_embeddings, orig._relative_position_to_absolute_position, orig._absolute_position_to_relative_position)
    make_attention_traceable()
    new = A.MultiHeadAttention
    g = torch.Generator().manual_seed(0)
    self = type("S", (), {"window_size": 10})()
    rel = torch.randn(1, 21, 96, generator=g)
    for l in (1, 5, 10, 11, 12, 37, 200, 341):
        x = torch.randn(2, 2, l, 2 * l - 1, generator=g)
        y = torch.randn(2, 2, l, l, generator=g)
        assert torch.allclose(saved[0](self, rel, l), new._get_relative_embeddings(self, rel, l)), ("embeddings", l)
        assert torch.allclose(saved[1](self, x), new._relative_position_to_absolute_position(self, x)), ("rel->abs", l)
        assert torch.allclose(saved[2](self, y), new._absolute_position_to_relative_position(self, y)), ("abs->rel", l)
    print("attention replacements match RVC's originals at lengths 1..341")


def build(version, config):
    from infer.module.models import SynthesizerTrnMs256NSFsid, SynthesizerTrnMs768NSFsid
    cls = SynthesizerTrnMs768NSFsid if version == "v2" else SynthesizerTrnMs256NSFsid
    return cls(*config, is_half=False)


class Export(nn.Module):
    """RVC's realtime inference with the random latent and the decode window as inputs.

    Inputs: phone [1,T,256|768] float, phone_lengths [1] int64, pitch [1,T] int64,
    pitchf [1,T] float, sid [1] int64, rnd [1,192,T] float (standard normal noise),
    skip_head [1] int64 and return_length [1] int64 (frames to decode, 10 ms each).
    Output: audio [1,1,return_length * sr/100].
    """

    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, phone, phone_lengths, pitch, pitchf, sid, rnd, skip_head, return_length):
        net = self.net
        g = net.emb_g(sid).unsqueeze(-1)
        m_p, logs_p, x_mask = net.enc_p(phone, pitch, phone_lengths)
        z_p = (m_p + torch.exp(logs_p) * rnd * 0.66666) * x_mask
        z = net.flow(z_p, x_mask, g=g, reverse=True)
        # Decode only the newest frames (the decoder is the expensive part). index_select with a
        # computed range keeps the window dynamic in the ONNX graph.
        idx = torch.arange(return_length[0], device=z.device) + skip_head[0]
        z = torch.index_select(z, 2, idx)
        x_mask = torch.index_select(x_mask, 2, idx)
        nsff0 = torch.index_select(pitchf, 1, idx)
        return net.dec(z * x_mask, nsff0, g=g)


def example_inputs(version, frames=200):
    dim = 768 if version == "v2" else 256
    g = torch.Generator().manual_seed(1)
    return (
        torch.randn(1, frames, dim, generator=g),
        torch.tensor([frames], dtype=torch.long),
        torch.randint(1, 255, (1, frames), generator=g),
        torch.rand(1, frames, generator=g) * 300 + 80,
        torch.tensor([0], dtype=torch.long),
        torch.randn(1, 192, frames, generator=g),
        torch.tensor([150], dtype=torch.long),
        torch.tensor([50], dtype=torch.long),
    )


NAMES = ["phone", "phone_lengths", "pitch", "pitchf", "sid", "rnd", "skip_head", "return_length"]


def export(net, version, path):
    net.eval()
    torch.onnx.export(
        Export(net), example_inputs(version), path,
        input_names=NAMES, output_names=["audio"],
        dynamic_axes={"phone": {1: "T"}, "pitch": {1: "T"}, "pitchf": {1: "T"}, "rnd": {2: "T"}, "audio": {2: "S"}},
        opset_version=17, do_constant_folding=False, dynamo=False,
    )


def strip(path, weights, out_path):
    """Empties every initializer that is a model weight; keeps true constants. Returns the names."""
    import onnx
    model = onnx.load(path)
    # The export wrapper holds the model as `self.net`, so the graph calls every weight
    # "net.<key>". Rename them to the bare .pth keys (and every node input that uses them).
    renames = {t.name: t.name[4:] for t in model.graph.initializer if t.name.startswith("net.")}
    for t in model.graph.initializer:
        t.name = renames.get(t.name, t.name)
    for node in model.graph.node:
        for i, name in enumerate(node.input):
            if name in renames:
                node.input[i] = renames[name]
    stripped, kept = [], []
    for t in model.graph.initializer:
        if t.name in weights:
            w = weights[t.name]
            assert list(t.dims) == list(w.shape), (t.name, list(t.dims), list(w.shape))
            t.ClearField("raw_data"); t.ClearField("float_data")
            stripped.append(t.name)
        else:
            kept.append(t.name)
    missing = sorted(set(weights) - set(stripped))
    onnx.save(model, out_path)
    return stripped, kept, missing


def model_weights(net):
    """The model's weights by the names the ONNX graph uses. Weight norm stays in the graph, so
    these are exactly the keys a .pth stores (weight_g / weight_v included): the app copies them
    straight across."""
    return {k: v.detach().numpy() for k, v in net.state_dict().items() if not k.startswith("enc_q.")}


def save_rvc_pth(net, version, config, path, sr_tag):
    """Saves a model exactly like RVC's training does (half precision, weight norm kept)."""
    sd = {k: v.half() for k, v in net.state_dict().items() if "enc_q" not in k}
    torch.save({"weight": sd, "config": config, "info": "WaveFix test model (random weights)",
                "sr": sr_tag, "f0": 1, "version": version}, path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rvc", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--test", action="store_true", help="also write a random test model and reference output")
    ap.add_argument("--only", nargs="*")
    args = ap.parse_args()
    sys.path.insert(0, args.rvc)
    os.makedirs(args.out, exist_ok=True)
    check_attention_patch()
    torch.manual_seed(0)

    index = {}
    for key, (version, config) in CONFIGS.items():
        if args.only and key not in args.only:
            continue
        net = build(version, config)
        del net.enc_q
        # Fresh layers start out identical (e.g. LayerNorm ones/zeros) and the exporter merges
        # identical tensors into one. Trained models never look like that; nudge every weight so
        # each keeps its own named slot in the graph.
        with torch.no_grad():
            for prm in net.parameters():
                prm.add_(torch.randn_like(prm) * 1e-3)
        full = os.path.join(args.out, key + ".full.onnx")
        export(net, version, full)
        weights = model_weights(net)
        stripped, kept, missing = strip(full, weights, os.path.join(args.out, key + ".onnx"))
        os.remove(full)
        print(f"{key}: {len(stripped)} weights emptied, {len(kept)} constants kept, missing {missing}")
        if missing:
            raise SystemExit(f"{key}: weights not found as initializers: {missing[:5]}")
        index[key] = {"version": version, "config": config, "weights": stripped}

        if args.test:
            test_fixture(net, version, config, key, args.out)

    with open(os.path.join(args.out, "templates.json"), "w") as f:
        json.dump(index, f, indent=1)


def test_fixture(net, version, config, key, out):
    """A random RVC-format .pth, plus the output PyTorch gives for fixed inputs with the
    synthesizer's internal randomness switched off (so ONNX Runtime can be compared exactly)."""
    import copy
    # Give the weight-norm parameters non-trivial values so folding them is really tested.
    with torch.no_grad():
        for name, p in net.named_parameters():
            if name.endswith("weight_g"):
                p.mul_(torch.rand_like(p) + 0.5)
    pth = os.path.join(out, f"test-{key}.pth")
    save_rvc_pth(net, version, config, pth, key.split("-")[1])

    ref = build(version, config)
    del ref.enc_q
    cpt = torch.load(pth, map_location="cpu")
    ref.load_state_dict(cpt["weight"], strict=False)
    ref = ref.float().eval()

    # Deterministic sine source: no random phase, no noise.
    import infer.module.models as M
    real_rand, real_randn_like = M.torch.rand, M.torch.randn_like
    M.torch.rand = lambda *a, **k: torch.zeros(*a, **{x: y for x, y in k.items() if x != "generator"})
    M.torch.randn_like = lambda t, **k: torch.zeros_like(t)
    try:
        inputs = example_inputs(version)
        with torch.no_grad():
            audio = Export(ref)(*inputs)
        det = os.path.join(out, f"test-{key}.det.onnx")
        export(ref, version, det + ".tmp")
        strip(det + ".tmp", model_weights(ref), det)
        os.remove(det + ".tmp")
    finally:
        M.torch.rand, M.torch.randn_like = real_rand, real_randn_like

    meta = {"inputs": {}, "output": {"shape": list(audio.shape)}}
    for name, t in zip(NAMES, inputs):
        arr = t.numpy()
        arr.tofile(os.path.join(out, f"test-{key}.{name}.bin"))
        meta["inputs"][name] = {"shape": list(arr.shape), "dtype": str(arr.dtype)}
    audio.numpy().astype(np.float32).tofile(os.path.join(out, f"test-{key}.audio.bin"))
    with open(os.path.join(out, f"test-{key}.json"), "w") as f:
        json.dump(meta, f, indent=1)
    print(f"{key}: test model + reference output written ({audio.shape[-1]} samples)")


if __name__ == "__main__":
    main()
