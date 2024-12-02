void cumulative_sum(unsigned int *arr, int width, int height) {
  for (int y = 0; y < height; y++) {
    arr[y * width] = arr[y * width] > 0 ? 1 : 0;
    for (int x = 1; x < width; x++) {
      arr[y * width + x] = arr[y * width + x - 1] + (arr[y * width + x] > 0 ? 1 : 0);
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
    hits[y] = 0;
    for (int x = 0; x < width - bw; x++) {
      if (arr[y * width + x] + arr[(y + bh) * width + x + bw] -
              arr[y * width + x + bw] - arr[(y + bh) * width + x] ==
          0) {
        hits[y]++;
      }
    }
  }

  for (int y = 1; y < height - bh; y++) {
    hits[y] += hits[y - 1];
  }
}
