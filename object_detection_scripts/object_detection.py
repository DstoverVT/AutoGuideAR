import os
import torch
import cv2
import numpy as np
import matplotlib.pyplot as plt

from datetime import datetime
from groundingdino.util.inference import load_model, load_image, predict, annotate
from PIL import Image


class DetectionException(Exception):
    """Exception encountered during or before object detection."""


class ObjectDetection:
    """Object detection using GroundingDINO model."""

    def __init__(self):
        """Setup GroundingDINO model.

        Prereq: Requires GroundingDINO repo to be cloned to current working directory and weights to be downloaded.
        """
        # self.HOME = os.path.dirname(os.path.abspath(__file__))
        # print(f"Home path: {self.HOME}")
        # self.CONFIG_PATH = os.path.join(
        #     self.HOME, "GroundingDINO/groundingdino/config/GroundingDINO_SwinT_OGC.py"
        # )
        self.CONFIG_PATH = (
            "GroundingDINO/groundingdino/config/GroundingDINO_SwinB_cfg.py"
        )
        print(self.CONFIG_PATH, "; exist:", os.path.isfile(self.CONFIG_PATH))
        # WEIGHTS_NAME = "groundingdino_swint_ogc.pth"
        WEIGHTS_NAME = "groundingdino_swinb_cogcoor.pth"
        self.WEIGHTS_PATH = os.path.join("weights", WEIGHTS_NAME)
        print(self.WEIGHTS_PATH, "; exist:", os.path.isfile(self.WEIGHTS_PATH))

        self.model = load_model(self.CONFIG_PATH, self.WEIGHTS_PATH)

    def setup_new_detection(self):
        self.detection_path: str = None
        self.images = None

    def _model_inference(
        self, images: tuple[np.array, torch.Tensor], text_prompt: str, threshold: float
    ):
        """Perform object dectection on image to get boxes, logits, and phrases."""
        print("Running model inference on image")
        # begin = time.time()
        TEXT_PROMPT = text_prompt
        BOX_TRESHOLD = threshold
        TEXT_TRESHOLD = threshold

        image_source = images[0]
        img_h = image_source.shape[0]
        img_w = image_source.shape[1]

        model_image = images[1]

        # Tensor of found boxes (with confidence above box_threshold)
        # Tensor of logits for text phrases
        # List[str] of phrases from prompt found corresponding to boxes (with confidence above text_threshold)
        boxes, logits, phrases = predict(
            model=self.model,
            image=model_image,
            caption=TEXT_PROMPT,
            box_threshold=BOX_TRESHOLD,
            text_threshold=TEXT_TRESHOLD,
        )

        # Get box coordinates
        scale_fct = torch.Tensor([img_w, img_h, img_w, img_h])
        boxes_scaled = boxes * scale_fct

        return boxes, boxes_scaled, logits, phrases

    def save_detection_to_plot(self, image):
        # Show output image with boxes and centers
        annotated_frame = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
        plt.figure(figsize=(16, 16))
        plt.imshow(annotated_frame)
        plt.axis("off")
        # Make file name
        now = datetime.now()
        timestamp = now.strftime("%m-%d_%H-%M-%S")
        detection_filename = "detection_result_" + timestamp + ".png"
        plt.savefig(detection_filename)
        self.detection_path = detection_filename

    def draw_raw_detection(
        self,
        model_output: tuple[torch.Tensor, torch.Tensor, torch.Tensor, list[str]],
    ):
        """Draw bounding boxes on input image."""
        boxes, boxes_scaled, logits, phrases = model_output

        annotated_frame = annotate(
            image_source=self.images[0], boxes=boxes, logits=logits, phrases=phrases
        )

        if boxes.numel() == 0:
            print("No objects detected.")

        for box in boxes_scaled:
            # Draw blue circle as center of each box (0, 0) is top-left of image
            annotated_frame = cv2.circle(
                annotated_frame, (int(box[0]), int(box[1])), 10, (255, 0, 0), -1
            )

        self.save_detection_to_plot(annotated_frame)

        # Color best box with a green dot
        # _, best_box, _ = max(zip(logits, boxes_scaled, phrases), key=lambda x: x[0])
        # annotated_frame = cv2.circle(
        #     annotated_frame,
        #     (int(best_box[0]), int(best_box[1])),
        #     10,
        #     (0, 255, 0),
        #     -1,
        # )

    def _get_image(self, image_path: str):
        """Load image for object detection. Uses image relative path"""
        if not os.path.exists(image_path):
            raise FileExistsError("Detector Error: Image file does not exist.")

        images = load_image(image_path)
        return images

    def __call__(
        self,
        image_path: str,
        prompt: str,
        threshold: float,
        draw: bool = False,
    ):
        """Detect objects on image using prompt and threshold for model."""
        self.images = self._get_image(image_path)
        model_output = self._model_inference(self.images, prompt, threshold)
        if draw:
            self.draw_raw_detection(model_output)

        return model_output


class ObjectDetectionInterface:

    def __init__(self):
        self.detector = ObjectDetection()
        # self.HOME = self.detector.HOME

    def _check_contains_box(self, box, other_output):
        """Check if box contains any of other_boxes. Return true if so."""
        x, y, w, h = box
        x_low, y_low = (x - w / 2, y - h / 2)
        x_high, y_high = (x + w / 2, y + h / 2)
        # Check if any other_boxes are inside box
        for _, other_box, _, _ in other_output:
            x_check, y_check = other_box[:2]
            # Box is inside current box
            if x_low < x_check < x_high and y_low < y_check < y_high:
                return True

        return False

    def _determine_best_box(
        self,
        detection_output: tuple[torch.Tensor, torch.Tensor, torch.Tensor, list[str]],
    ) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor, str]:
        """Gets best box from object detection given all boxes, confidences, and phrases.

        Args:
        - detection_output: All the boxes from detection in the form (boxes_unscaled, boxes, confidences, phrases)

        Returns:
            - Best box in the form of (box_unscaled, box, confidence, phrase)
        """
        # Contains only boxes that don't contain other boxes inside it
        boxes_unscaled, boxes, confidences, phrases = detection_output

        kept_results = list(zip(boxes_unscaled, boxes, confidences, phrases))
        # Sort from lowest to highest confidence (ascending order) to rid of lowest confidence boxes first
        kept_results.sort(key=lambda x: x[2])
        # Store all boxes in same order as kept_results (lowest to highest confidence)
        all_boxes = [result[1] for result in kept_results]
        # Index that keeps track of the current box (in loop) in kept_results
        current_box_in_kept = 0

        # Disregard boxes with any other box inside of it
        for box in all_boxes:
            other_results = (
                kept_results[:current_box_in_kept]
                + kept_results[current_box_in_kept + 1 :]
            )
            # Remove box from kept_boxes if it contains any other box
            if self._check_contains_box(box, other_results):
                kept_results.pop(current_box_in_kept)
            else:
                current_box_in_kept += 1

        # Should at least have one box left if get to this point
        assert len(kept_results) > 0

        # Return box with highest confidence
        best_results = max(kept_results, key=lambda x: x[2])

        # Draws all box centers as blue dots and best box center as green dot
        # Note: Draws on existing plot from ObjectDetection which includes all boxes detected, but only
        # centers of kept boxes will be drawn
        self.draw_detection_output(kept_results, best_results)

        return best_results

    def region_containing_all_boxes(self, boxes):
        """Output box containing all bounding boxes in image."""
        top_left_coords = [(x - w / 2, y - h / 2) for x, y, w, h in boxes]
        bottom_right_coords = [(x + w / 2, y + h / 2) for x, y, w, h in boxes]

        smallest_x = min(top_left_coords, key=lambda x: x[0])[0].item()
        smallest_y = min(top_left_coords, key=lambda y: y[1])[1].item()

        largest_x = max(bottom_right_coords, key=lambda x: x[0])[0].item()
        largest_y = max(bottom_right_coords, key=lambda y: y[1])[1].item()

        return (smallest_x, smallest_y, largest_x, largest_y)

    def crop_image_to_box(self, box, image_path) -> str:
        """Crops an image to a bounding box.

        Args:
        - box: Bounding box is in (x1, y1, x2, y2) where (x1, y1) is top-left and (x2, y2) is bottom-right
        - image_path: Image to crop, must exist

        Returns:
        - path to cropped image created
        """
        if not os.path.exists(image_path):
            raise DetectionException("Image must exist to crop it.")

        image = Image.open(image_path)
        cropped_image = image.crop(box)

        now = datetime.now()
        timestamp = now.strftime("%m-%d_%H-%M-%S")
        new_filename = "cropped_image_" + timestamp + ".jpg"
        cropped_image.save(new_filename)
        return new_filename

    def draw_detection_output(self, kept_output, best_output):
        """Add a circle to current detection plot."""
        # Add to current plot from ObjectDetector
        boxes_unscaled, boxes, logits, phrases = zip(*kept_output)
        boxes_unscaled = torch.stack(boxes_unscaled)
        logits = torch.stack(logits)
        phrases = list(phrases)

        annotated_frame = annotate(
            image_source=self.detector.images[0],
            boxes=boxes_unscaled,
            logits=logits,
            phrases=phrases,
        )

        for box in boxes:
            # Draw blue circle as center of each box (0, 0) is top-left of image
            annotated_frame = cv2.circle(
                annotated_frame, (int(box[0]), int(box[1])), 10, (255, 0, 0), -1
            )

        best_box = best_output[1]
        # Draw green dot for best box
        annotated_frame = cv2.circle(
            annotated_frame,
            (int(best_box[0]), int(best_box[1])),
            10,
            (0, 255, 0),
            -1,
        )

        # Save output image with boxes and centers
        self.detector.save_detection_to_plot(annotated_frame)

    def run_object_detection(
        self, filepath, text_prompt, box_threshold, draw_raw=False
    ) -> tuple[torch.Tensor, torch.Tensor, torch.Tensor, list[str]]:
        """Run GroundingDINO on file path specified.
        Args:
        - draw_raw: If true, draws direct output from GroundingDINO onto a plot

        Returns:
        - Output of model: Tuple of (boxes_unscaled, boxes, confidences, phrases)
        """
        # Run model on image
        self.detector.setup_new_detection()
        try:
            result = self.detector(
                filepath,
                # prompt="helmet . computer . bottle . table . mouse . keyboard . controller . knob . button . microwave",
                text_prompt,
                box_threshold,
                draw_raw,
            )
        except Exception as e:
            raise DetectionException(f"{type(e).__name__}: {e}")

        return result

    def run_object_detection_with_crop(
        self, filepath, text_prompt, first_threshold, second_threshold
    ):
        """Steps:
        - Runs detection on input image
        - Crops to region containing all bounding boxes
        - Runs detection again on cropped image
        - Outputs highest confidence result
        """
        # First pass saves raw detection output to plot
        _, boxes_pass1, _, _ = self.run_object_detection(
            filepath, text_prompt, first_threshold, draw_raw=True
        )

        if boxes_pass1.numel() == 0:
            print("No objects detected during first object detection pass.")
            return None, None, None

        # Run object detection again after cropping image to largest box
        region = self.region_containing_all_boxes(boxes_pass1)
        cropped_image_path = self.crop_image_to_box(region, filepath)
        detection_output = self.run_object_detection(
            cropped_image_path, text_prompt, second_threshold, draw_raw=True
        )

        _, boxes, confidences, phrases = detection_output
        if boxes.numel() == 0:
            print("No objects detected during second object detection pass")
            return None, None, None

        for box, confidence, phrase in zip(boxes, confidences, phrases):
            print(f"{phrase}: confidence {confidence.tolist()}, box {box.tolist()}")

        # This will also draw detection results to plot and save it
        _, best_box, confidence, best_phrase = self._determine_best_box(
            detection_output
        )

        print(
            f"SELECTED BOX:\nconfidence: {confidence}\nbox: {best_box.tolist()}\nphrase: {best_phrase}"
        )

        center = best_box.tolist()[:2]
        top_left_coord = region[:2]

        return center, top_left_coord, cropped_image_path

    def prime_detection_with_test(self):
        test_filepath = "data/HL_coffee_pic.jpg"
        self.run_object_detection(test_filepath, "test", 0.1)


# MAIN
if __name__ == "__main__":
    detector = ObjectDetection()
    IMAGE_REL_PATH = "data/HL_microwave_close.jpg"
    detector(IMAGE_REL_PATH, "button", 0.2, draw=True)
