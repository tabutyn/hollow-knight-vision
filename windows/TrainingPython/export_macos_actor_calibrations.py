"""Export an existing Mac training run and its optional actor heatmaps, without training."""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys
from tempfile import TemporaryDirectory
from uuid import UUID

from coremltools.proto import Model_pb2
from onnx import TensorProto, checker, helper
import torch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "macos" / "TrainingPython"))
from hkv_incremental_trainer import ALGORITHM, HKVDetector, export_onnx


def child(directory: Path, filename: str) -> Path:
    if not filename or filename in {".", ".."} or any(character in filename for character in "/\\:"):
        raise ValueError(f"Expected a filename inside {directory}")
    return directory / filename


def onnx_heatmap(data: bytes) -> bytes:
    spec = Model_pb2.Model.FromString(data)
    if spec.WhichOneof("Type") != "neuralNetwork":
        raise ValueError("The actor localizer must be a Core ML neural network")
    source = spec.description.input
    output = spec.description.output
    if len(source) != 1 or source[0].name != "image" or source[0].type.WhichOneof("Type") != "imageType":
        raise ValueError("The actor localizer must take one RGB image")
    image = source[0].type.imageType
    if (image.width, image.height, image.colorSpace) != (640, 360, image.RGB) or [item.name for item in output] != ["heatmap"]:
        raise ValueError("Expected a 640x360 RGB image and one heatmap")
    network = spec.neuralNetwork
    if len(network.preprocessing) != 1 or network.preprocessing[0].WhichOneof("preprocessor") != "scaler":
        raise ValueError("Unsupported Core ML image preprocessing")
    scaler = network.preprocessing[0].scaler
    nodes = []
    initializers = []

    def constant(name, values, shape=()):
        initializers.append(helper.make_tensor(name, TensorProto.FLOAT, shape, values))
        return name

    # The Windows detector already converts the image to an RGB float tensor in [0, 1].
    scale = constant("hkv_image_scale", [scaler.channelScale * 255])
    bias = constant("hkv_image_bias", [scaler.redBias, scaler.greenBias, scaler.blueBias], [1, 3, 1, 1])
    nodes.append(helper.make_node("Mul", ["image", scale], ["hkv_image_scaled"]))
    nodes.append(helper.make_node("Add", ["hkv_image_scaled", bias], ["hkv_image_normalized"]))

    for index, layer in enumerate(network.layers):
        name = f"hkv_layer_{index}"
        inputs = ["hkv_image_normalized" if item == "image" else item for item in layer.input]
        outputs = list(layer.output)
        kind = layer.WhichOneof("layer")
        if kind == "loadConstantND":
            constant(outputs[0], layer.loadConstantND.data.floatValue, list(layer.loadConstantND.shape))
        elif kind == "add" and len(inputs) == 2:
            nodes.append(helper.make_node("Add", inputs, outputs))
        elif kind == "multiply" and len(inputs) == 1:
            nodes.append(helper.make_node("Mul", [inputs[0], constant(name, [layer.multiply.alpha])], outputs))
        elif kind == "activation":
            activation = layer.activation.WhichOneof("NonlinearityType")
            if activation == "linear":
                coefficients = layer.activation.linear
                nodes.append(helper.make_node("Mul", [inputs[0], constant(name + "_scale", [coefficients.alpha])], [name]))
                nodes.append(helper.make_node("Add", [name, constant(name + "_bias", [coefficients.beta])], outputs))
            elif activation in {"ReLU", "sigmoid"}:
                nodes.append(helper.make_node("Relu" if activation == "ReLU" else "Sigmoid", inputs, outputs))
            else:
                raise ValueError(f"Unsupported heatmap activation {activation}")
        elif kind == "convolution":
            conv = layer.convolution
            if conv.isDeconvolution or conv.WhichOneof("ConvolutionPaddingType") != "valid":
                raise ValueError("Unsupported heatmap convolution")
            borders = conv.valid.paddingAmounts.borderAmounts
            pads = [edge.startEdgeSize for edge in borders] + [edge.endEdgeSize for edge in borders]
            weights = constant(name + "_weights", conv.weights.floatValue, [conv.outputChannels, conv.kernelChannels, *conv.kernelSize])
            inputs.append(weights)
            if conv.hasBias:
                inputs.append(constant(name + "_bias", conv.bias.floatValue, [conv.outputChannels]))
            nodes.append(helper.make_node("Conv", inputs, outputs, strides=list(conv.stride), pads=pads,
                                          dilations=list(conv.dilationFactor), group=conv.nGroups or 1))
        else:
            raise ValueError(f"Unsupported heatmap layer {kind}")

    graph = helper.make_graph(nodes, "HollowKnightVisionActorHeatmap",
        [helper.make_tensor_value_info("image", TensorProto.FLOAT, [1, 3, 360, 640])],
        [helper.make_tensor_value_info("heatmap", TensorProto.FLOAT, [1, 1, 90, 160])], initializers)
    model = helper.make_model(graph, ir_version=9, opset_imports=[helper.make_opsetid("", 17)])
    checker.check_model(model, full_check=True)
    return model.SerializeToString()


def export(source_run: Path, source_calibrations: Path, destination: Path) -> None:
    if destination.exists():
        raise FileExistsError(destination)
    metadata = json.loads((source_run / "training.json").read_bytes())
    run = UUID(metadata["id"])
    checkpoint_path = child(source_run, metadata["checkpointFilename"])
    checkpoint = torch.load(checkpoint_path, map_location="cpu", weights_only=True)
    if checkpoint["algorithm"] != ALGORITHM or checkpoint["class_names"] != metadata["classIdentifiers"]:
        raise ValueError("The detector checkpoint does not match the training receipt")
    if "trainedClassIdentifiers" not in metadata:
        raise ValueError("The source run must record its trained classes")

    with TemporaryDirectory(dir=destination.parent, prefix="actor-calibrations-") as temporary:
        staging = Path(temporary) / "export"
        model = staging / "detector"
        calibrations = staging / "calibrations"
        model.mkdir(parents=True)
        calibrations.mkdir()
        detector = HKVDetector(len(checkpoint["class_names"]), pretrained=False)
        detector.load_state_dict(checkpoint["state_dict"])
        export_onnx(detector, model / "Detector.onnx")
        shutil.copyfile(checkpoint_path, child(model, checkpoint_path.name))
        metadata.update(modelFilename="Detector.onnx", modelFormat="onnx")
        if predictions := metadata.get("predictionsFilename"):
            shutil.copyfile(child(source_run, predictions), child(model, predictions))
        (model / "training.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")

        for name in ("knight", "crawlid", "geo-deposit"):
            filename = f"{name}-{run}.json"
            source = source_calibrations / filename
            if not source.is_file():
                continue
            receipt = json.loads(source.read_bytes())
            if UUID(receipt["sourceRunIdentifier"]) != run:
                raise ValueError(f"Calibration run mismatch in {source.name}")
            if not (localizer := receipt.get("localizer")):
                continue
            data = child(source_calibrations, localizer["modelFilename"]).read_bytes()
            if hashlib.sha256(data).hexdigest() != localizer["sha256"].lower():
                raise ValueError(f"The heatmap does not match {source.name}")
            converted = onnx_heatmap(data)
            digest = hashlib.sha256(converted).hexdigest()
            model_name = f"{name}-{digest[:12]}.onnx"
            (calibrations / model_name).write_bytes(converted)
            localizer.update(modelFilename=model_name, sha256=digest)
            (calibrations / filename).write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")

        if destination.exists():
            raise FileExistsError(destination)
        staging.rename(destination)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_run", type=Path)
    parser.add_argument("source_calibrations", type=Path)
    parser.add_argument("destination", type=Path)
    arguments = parser.parse_args()
    export(arguments.source_run, arguments.source_calibrations, arguments.destination)
