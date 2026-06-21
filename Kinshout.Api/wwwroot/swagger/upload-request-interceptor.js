window.kinshoutUploadRequestInterceptor = function (req) {
  var url = (req.url || "").toLowerCase();
  if (url.indexOf("/api/uploads/") === -1) {
    return req;
  }

  var clientToken =
    (req.headers && (req.headers["X-Kinshout-Client-Token"] || req.headers["x-kinshout-client-token"])) ||
    null;

  if (!clientToken && window.ui && window.ui.getState) {
    var state = window.ui.getState();
    var auth = state && state.get && state.get("auth");
    var authorized = auth && auth.get && auth.get("authorized");
    if (authorized && authorized.ClientToken && authorized.ClientToken.value) {
      clientToken = authorized.ClientToken.value;
    } else if (authorized && authorized.toJS) {
      var plain = authorized.toJS();
      if (plain && plain.ClientToken && plain.ClientToken.value) {
        clientToken = plain.ClientToken.value;
      }
    }
  }

  if (clientToken && req.body instanceof FormData) {
    req.body.delete("x_kinshout_client_token");
    req.body.append("x_kinshout_client_token", clientToken);
  }

  if (req.headers) {
    var ct = req.headers["Content-Type"] || req.headers["content-type"];
    if (ct === "multipart/form-data") {
      delete req.headers["Content-Type"];
      delete req.headers["content-type"];
    }
  }

  return req;
};
