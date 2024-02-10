void cumulative_sum(int *arr, int width, int height) {
  for (int y = 0; y < height; y++) {
    for (int x = 1; x < width; x++) {
      arr[y * width + x] += arr[y * width + x - 1];
    }
  }
  for (int y = 1; y < height; y++) {
    for (int x = 0; x < width; x++) {
      arr[y * width + x] += arr[(y - 1) * width + x];
    }
  }
}

void hit_count(int *arr, int width, int height, int bw, int bh, int *hits) {
  for (int y = 0; y < height - bh; y++) {
    for (int x = 0; x < width - bw; x++) {
      if (arr[y * width + x] + arr[(y + bh) * width + x + bw] -
              arr[y * width + x + bw] - arr[(y + bh) * width + x] ==
          0) {
        hits[y]++;
      }
    }
  }
}
