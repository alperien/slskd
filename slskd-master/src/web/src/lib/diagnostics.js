import api from './api';

export const getTransferDiagnostics = async () => {
  const response = await api.get('/diagnostics/transfers');
  return response.data;
};
